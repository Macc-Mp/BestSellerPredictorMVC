// Services/MLModelTrainer.cs
using Microsoft.ML; // Add this if not present
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.LightGbm;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq; // For .Any()
using BestSellerPredictorMVC.Models;

public class MLModelTrainer
{
    private readonly MLContext _mlContext;
    private readonly string _modelPath;
    private readonly ILogger? _logger;

    public MLModelTrainer(string modelPath, ILogger? logger = null)
    {
        _mlContext = new MLContext(seed: 0); // Seed for reproducibility  
        _modelPath = modelPath;
        _logger = logger;
    }

    // Return nullable types so callers can detect failures explicitly
    public (ITransformer? Model, MulticlassClassificationMetrics? Metrics) TrainAndSaveModel(IEnumerable<ProductSalesData> trainingData)
    {
        // Let exceptions bubble to caller (controller) so they are logged in the app's logger.
        if (trainingData == null)
        {
            _logger?.LogWarning("TrainAndSaveModel: trainingData is null.");
            return (null, null);
        }

        var trainingList = trainingData.ToList();
        _logger?.LogInformation("TrainAndSaveModel: received {Count} training records.", trainingList.Count);

        if (!trainingList.Any())
        {
            _logger?.LogWarning("No training data provided. Cannot train model.");
            return (null, null);
        }

        var labels = trainingList
            .Select(t => (t.SalePerformanceCategory ?? string.Empty).Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .ToList();

        _logger?.LogInformation("Distinct labels found: {Count} => {Labels}", labels.Count, string.Join(", ", labels));

        if (labels.Count < 2)
        {
            _logger?.LogWarning("Training requires at least 2 distinct label classes. Aborting training.");
            return (null, null);
        }

        var unitPriceVariance = trainingList.Select(t => t.UnitPrice).Distinct().Count();
        var stockVariance = trainingList.Select(t => t.CurrentStock).Distinct().Count();
        if (unitPriceVariance <= 1)
            _logger?.LogWarning("Warning: UnitPrice has no variance across training data.");
        if (stockVariance <= 1)
            _logger?.LogWarning("Warning: CurrentStock has no variance across training data.");

        IDataView dataView = _mlContext.Data.LoadFromEnumerable(trainingList);

        // Apply MapValueToKey to create the 'Label' key column before printing label order
        var keyPipeline = _mlContext.Transforms.Conversion.MapValueToKey("Label", "SalePerformanceCategory");
        var keyedDataView = keyPipeline.Fit(dataView).Transform(dataView);

        // Print the label order used in the pipeline (for debugging)
        PrintLabelOrder(_mlContext, keyedDataView, "Label");

        var dataProcessPipeline = _mlContext.Transforms.Conversion.MapValueToKey("Label", "SalePerformanceCategory")
            .Append(_mlContext.Transforms.Text.FeaturizeText("ProductNameFeaturized", "ProductName"))
            .Append(_mlContext.Transforms.Categorical.OneHotEncoding("CategoryEncoded", "Category"))
            .Append(_mlContext.Transforms.Concatenate("Features",
                "ProductNameFeaturized",
                "CategoryEncoded",
                "UnitPrice",
                "CurrentStock")); // Ensure all features are included  

        var trainer = _mlContext.MulticlassClassification.Trainers.LightGbm(
            new LightGbmMulticlassTrainer.Options
            {
                NumberOfIterations = 100,
                LearningRate = 0.1,
                NumberOfLeaves = 32,
                MinimumExampleCountPerLeaf = 10,
                LabelColumnName = "Label",
                FeatureColumnName = "Features"
            });

        var trainingPipeline = dataProcessPipeline.Append(trainer)
            .Append(_mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

        _logger?.LogInformation("Starting model training...");
        ITransformer model = trainingPipeline.Fit(dataView);
        _logger?.LogInformation("Model training complete.");

        // Evaluate the model (best-effort)
        MulticlassClassificationMetrics? metrics = null;
        try
        {
            var predictions = model.Transform(dataView);
            metrics = _mlContext.MulticlassClassification.Evaluate(predictions, labelColumnName: "Label");
        }
        catch (Exception evalEx)
        {
            _logger?.LogWarning(evalEx, "Model evaluation failed: {Message}", evalEx.Message);
        }

        if (metrics != null)
        {
            _logger?.LogInformation("Model Evaluation Metrics: MicroAccuracy={Micro:F4} MacroAccuracy={Macro:F4} LogLoss={LogLoss:F4}",
                metrics.MicroAccuracy, metrics.MacroAccuracy, metrics.LogLoss);
        }
        else
        {
            _logger?.LogInformation("Metrics are null (evaluation failed or not performed).");
        }

        // Save the trained model — surface any save errors to caller
        try
        {
            _mlContext.Model.Save(model, dataView.Schema, _modelPath);
            _logger?.LogInformation("Model saved to: {Path}", _modelPath);
        }
        catch (Exception saveEx)
        {
            _logger?.LogError(saveEx, "Failed to save model to {Path}: {Message}", _modelPath, saveEx.Message);
            // Rethrow so the controller can detect failure and avoid setting session to a non-existent model
            throw;
        }

        return (model, metrics);
    }

    private void PrintLabelOrder(MLContext mlContext, IDataView dataView, string labelColumnName)
    {
        try
        {
            var labelColumn = dataView.Schema[labelColumnName];
            VBuffer<ReadOnlyMemory<char>> labelBuffer = default;
            labelColumn.GetKeyValues(ref labelBuffer);

            _logger?.LogInformation("Label order used in the model (Score array index -> label):");
            int idx = 0;
            foreach (var label in labelBuffer.DenseValues())
            {
                _logger?.LogInformation("  [{Index}] {Label}", idx, label.ToString());
                idx++;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "PrintLabelOrder: failed to print label order: {Message}", ex.Message);
        }
    }
}
