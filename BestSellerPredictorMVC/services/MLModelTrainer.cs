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

    public (ITransformer Model, MulticlassClassificationMetrics Metrics) TrainAndSaveModel(IEnumerable<ProductSalesData> trainingData)
    {
        // Ensure there's data to train on  
        if (trainingData == null || !trainingData.Any())
        {
            Console.WriteLine("No training data provided. Cannot train model.");
            return (null, null);
        }

        IDataView dataView = _mlContext.Data.LoadFromEnumerable(trainingData);

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
        var predictions = model.Transform(dataView);
        var metrics = _mlContext.MulticlassClassification.Evaluate(predictions, "Label");

        Console.WriteLine($"\nModel Evaluation Metrics:");
        Console.WriteLine($"  MicroAccuracy: {metrics.MicroAccuracy:F4} (Overall correctness)");
        Console.WriteLine($"  MacroAccuracy: {metrics.MacroAccuracy:F4} (Average correctness per class)");
        Console.WriteLine($"  LogLoss: {metrics.LogLoss:F4} (Lower is better, penalizes confident wrong predictions)");

        // Model Evaluation Metrics explanation:
        // - MicroAccuracy: Measures overall accuracy by counting the total number of correct predictions across all classes and dividing by the total number of predictions. It treats every prediction equally, regardless of class.
        // - MacroAccuracy: Calculates the accuracy for each class independently, then averages these accuracies. It gives equal weight to each class, regardless of how many samples are in each class.
        // Use MicroAccuracy to understand overall correctness.
        // Use MacroAccuracy to see how well the model performs on each class, especially if your data is imbalanced.

        // Save the trained model  
        _mlContext.Model.Save(model, dataView.Schema, _modelPath);
        Console.WriteLine($"Model saved to: {_modelPath}");

        return (model, metrics);
    }

    private void PrintLabelOrder(MLContext mlContext, IDataView dataView, string labelColumnName)
    {
        // Retrieve the column from the schema using the labelColumnName
        var labelColumn = dataView.Schema[labelColumnName];

        // Initialize the VBuffer to hold the key values
        VBuffer<ReadOnlyMemory<char>> labelBuffer = default;

        // FIX: Call GetKeyValues directly on labelColumn, not on labelColumn.Annotations
        labelColumn.GetKeyValues(ref labelBuffer);

        Console.WriteLine("Label order used in the model (Score array index -> label):");
        int idx = 0;
        foreach (var label in labelBuffer.DenseValues())
        {
            Console.WriteLine($"  [{idx}] {label.ToString()}");
            idx++;
        }
    }
}
