// Services/MLModelTrainer.cs
using Microsoft.ML; // Add this if not present
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.LightGbm;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq; // For .Any()
using BestSellerPredictorMVC.Models;

public class MLModelTrainer
{
    private readonly MLContext _mlContext;
    private readonly string _modelPath;

    public MLModelTrainer(string modelPath)
    {
        _mlContext = new MLContext(seed: 0); // Seed for reproducibility  
        _modelPath = modelPath;
    }

    // Return nullable types so callers can detect failures explicitly
    public (ITransformer? Model, MulticlassClassificationMetrics? Metrics) TrainAndSaveModel(IEnumerable<ProductSalesData> trainingData)
    {
        try
        {
            // Ensure there's data to train on  
            if (trainingData == null)
            {
                Console.WriteLine("TrainAndSaveModel: trainingData is null.");
                return (null, null);
            }

            var trainingList = trainingData.ToList();
            Console.WriteLine($"TrainAndSaveModel: received {trainingList.Count} training records.");

            if (!trainingList.Any())
            {
                Console.WriteLine("No training data provided. Cannot train model.");
                return (null, null);
            }

            // Check label cardinality
            var labels = trainingList
                .Select(t => (t.SalePerformanceCategory ?? string.Empty).Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .ToList();

            Console.WriteLine($"Distinct labels found: {labels.Count} => [{string.Join(", ", labels)}]");

            if (labels.Count < 2)
            {
                Console.WriteLine("Training requires at least 2 distinct label classes. Aborting training.");
                return (null, null);
            }

            // Basic feature checks (warn if zero variance)
            var unitPriceVariance = trainingList.Select(t => t.UnitPrice).Distinct().Count();
            var stockVariance = trainingList.Select(t => t.CurrentStock).Distinct().Count();
            if (unitPriceVariance <= 1)
                Console.WriteLine("Warning: UnitPrice has no variance across training data.");
            if (stockVariance <= 1)
                Console.WriteLine("Warning: CurrentStock has no variance across training data.");

            IDataView dataView = _mlContext.Data.LoadFromEnumerable(trainingList);

            // Apply MapValueToKey to create the 'Label' key column before printing label order
            var keyPipeline = _mlContext.Transforms.Conversion.MapValueToKey("Label", "SalePerformanceCategory");
            var keyedDataView = keyPipeline.Fit(dataView).Transform(dataView);

            // Print the label order used in the pipeline (for debugging)
            PrintLabelOrder(_mlContext, keyedDataView, "Label");

            // Define the data processing and training pipeline  
            var dataProcessPipeline = _mlContext.Transforms.Conversion.MapValueToKey("Label", "SalePerformanceCategory")
                .Append(_mlContext.Transforms.Text.FeaturizeText("ProductNameFeaturized", "ProductName"))
                .Append(_mlContext.Transforms.Categorical.OneHotEncoding("CategoryEncoded", "Category"))
                .Append(_mlContext.Transforms.Concatenate("Features",
                    "ProductNameFeaturized",
                    "CategoryEncoded",
                    "UnitPrice",
                    "CurrentStock")); // Ensure all features are included  

            // Choose a multi-class classification trainer (LightGbm is generally robust)  
            var trainer = _mlContext.MulticlassClassification.Trainers.LightGbm(
                new LightGbmMulticlassTrainer.Options
                {
                    NumberOfIterations = 100, // Number of boosting iterations  
                    LearningRate = 0.1,       // Step size shrinkage  
                    NumberOfLeaves = 32,      // Max number of leaves in one tree  
                    MinimumExampleCountPerLeaf = 10, // Min data needed to form a leaf  
                    LabelColumnName = "Label",
                    FeatureColumnName = "Features"
                });

            var trainingPipeline = dataProcessPipeline.Append(trainer)
                .Append(_mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel")); // Map key back to original string label  

            Console.WriteLine("Starting model training...");
            ITransformer model = trainingPipeline.Fit(dataView);
            Console.WriteLine("Model training complete.");

            // Evaluate the model (important for understanding performance)  
            Console.WriteLine("Evaluating model performance...");
            MulticlassClassificationMetrics? metrics = null;
            try
            {
                var predictions = model.Transform(dataView);
                metrics = _mlContext.MulticlassClassification.Evaluate(predictions, labelColumnName: "Label");
            }
            catch (Exception evalEx)
            {
                Console.WriteLine($"Model evaluation failed: {evalEx.Message}");
                // Still attempt to save the model; return null metrics to indicate evaluation problem
            }

            if (metrics != null)
            {
                Console.WriteLine($"\nModel Evaluation Metrics:");
                Console.WriteLine($"  MicroAccuracy: {metrics.MicroAccuracy:F4} (Overall correctness)");
                Console.WriteLine($"  MacroAccuracy: {metrics.MacroAccuracy:F4} (Average correctness per class)");
                Console.WriteLine($"  LogLoss: {metrics.LogLoss:F4} (Lower is better, penalizes confident wrong predictions)");
            }
            else
            {
                Console.WriteLine("Metrics are null (evaluation failed or not performed).");
            }

            // Save the trained model  
            try
            {
                _mlContext.Model.Save(model, dataView.Schema, _modelPath);
                Console.WriteLine($"Model saved to: {_modelPath}");
            }
            catch (Exception saveEx)
            {
                Console.WriteLine($"Failed to save model: {saveEx.Message}");
            }

            return (model, metrics);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"TrainAndSaveModel exception: {ex.Message}");
            return (null, null);
        }
    }

    private void PrintLabelOrder(MLContext mlContext, IDataView dataView, string labelColumnName)
    {
        try
        {
            // Retrieve the column from the schema using the labelColumnName
            var labelColumn = dataView.Schema[labelColumnName];

            // Initialize the VBuffer to hold the key values
            VBuffer<ReadOnlyMemory<char>> labelBuffer = default;

            // Call GetKeyValues directly on labelColumn to get ordered label mapping
            labelColumn.GetKeyValues(ref labelBuffer);

            Console.WriteLine("Label order used in the model (Score array index -> label):");
            int idx = 0;
            foreach (var label in labelBuffer.DenseValues())
            {
                Console.WriteLine($"  [{idx}] {label.ToString()}");
                idx++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PrintLabelOrder: failed to print label order: {ex.Message}");
        }
    }
}
