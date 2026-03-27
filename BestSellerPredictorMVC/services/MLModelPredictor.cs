// Services/MLModelPredictor.cs
using Microsoft.ML; // Add this if not present
using Microsoft.ML.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq; // For .Any()
using BestSellerPredictorMVC.Models;
public class MLModelPredictor
{
    private readonly MLContext _mlContext;
    private readonly ITransformer _trainedModel;
    private readonly string[]? _scoreLabels;

    public MLModelPredictor(string modelPath)
    {
        _mlContext = new MLContext();
        // Check if model file exists before loading
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"ML model file not found at: {modelPath}. Please train the model first by running the application with training data.");
        }
        _trainedModel = _mlContext.Model.Load(modelPath, out var modelSchema);

        // Try to extract slot/label names for the Score column from the model schema so we can map Score[] indices to labels.
        try
        {
            if (modelSchema != null)
            {
                int scoreIndex = -1;
                for (int i = 0; i < modelSchema.Count; i++)
                {
                    if (string.Equals(modelSchema[i].Name, "Score", StringComparison.OrdinalIgnoreCase))
                    {
                        scoreIndex = i;
                        break;
                    }
                }

                if (scoreIndex >= 0)
                {
                    var scoreColumn = modelSchema[scoreIndex];
                    VBuffer<ReadOnlyMemory<char>> slotNames = default;
                    if (scoreColumn.HasSlotNames())
                    {
                        scoreColumn.GetSlotNames(ref slotNames);
                        _scoreLabels = slotNames.DenseValues().Select(x => x.ToString()).ToArray();
                    }
                }
            }
        }
        catch
        {
            // ignore — predictor will still return scores without labels
            _scoreLabels = null;
        }
    }

    // Predict for a single product
    public ProductSalePrediction Predict(ProductSalesData productData)
    {
        var predEngine = _mlContext.Model.CreatePredictionEngine<ProductSalesData, ProductSalePrediction>(_trainedModel);
        var prediction = predEngine.Predict(productData);

        // If we extracted score labels, attach them to the prediction for UI rendering
        if (_scoreLabels != null)
            prediction.ScoreLabels = _scoreLabels;

        // Example usage of the Score property after prediction
        Console.WriteLine("Predicted: " + prediction.PredictedSalePerformanceCategory);
        if (prediction.Score != null)
        {
            // To ensure correct mapping between Score[] and class labels, retrieve the label order from the model's schema if possible.
            // If not, document the label order used during training and use it consistently in your code.
            // Example: string[] classLabels = new[] { "Underperforming", "BestSeller", "NormalPerformer" };
            string[] classLabels = new[] { "Underperforming", "BestSeller", "NormalPerformer" };
            for (int i = 0; i < prediction.Score.Length && i < classLabels.Length; i++)
            {
                Console.WriteLine($"{classLabels[i]}: {prediction.Score[i]:F4}");
            }
        }

        return prediction;
    }

    // Predict for a batch of products (more efficient for many predictions)
    public IEnumerable<ProductSalePrediction> PredictBatch(IEnumerable<ProductSalesData> productDataList)
    {
        if (productDataList == null || !productDataList.Any())
        {
            return Enumerable.Empty<ProductSalePrediction>();
        }
        IDataView dataView = _mlContext.Data.LoadFromEnumerable(productDataList);
        IDataView predictions = _trainedModel.Transform(dataView);
        // Ignore missing columns (like ScoreLabels) when materializing predictions into the POCO
        var preds = _mlContext.Data.CreateEnumerable<ProductSalePrediction>(predictions, reuseRowObject: false, ignoreMissingColumns: true).ToList();

        if (_scoreLabels != null && preds.Any())
        {
            foreach (var p in preds)
            {
                p.ScoreLabels = _scoreLabels;
            }
        }

        return preds;
    }
}
