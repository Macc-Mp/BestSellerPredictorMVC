// Services/MLModelPredictor.cs
using Microsoft.ML; // Add this if not present
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq; // For .Any()
using BestSellerPredictorMVC.Models;
public class MLModelPredictor
{
    private readonly MLContext _mlContext;
    private readonly ITransformer _trainedModel;

    public MLModelPredictor(string modelPath)
    {
        _mlContext = new MLContext();
        // Check if model file exists before loading
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"ML model file not found at: {modelPath}. Please train the model first by running the application with training data.");
        }
        _trainedModel = _mlContext.Model.Load(modelPath, out var modelSchema);
    }

    // Predict for a single product
    public ProductSalePrediction Predict(ProductSalesData productData)
    {
        var predEngine = _mlContext.Model.CreatePredictionEngine<ProductSalesData, ProductSalePrediction>(_trainedModel);
        var prediction = predEngine.Predict(productData);

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
        return _mlContext.Data.CreateEnumerable<ProductSalePrediction>(predictions, reuseRowObject: false);
    }
}
