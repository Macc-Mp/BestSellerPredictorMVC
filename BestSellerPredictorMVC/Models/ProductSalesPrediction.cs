// DataModels/ProductSalePrediction.cs
using Microsoft.ML.Data; // Add this using statement

public class ProductSalePrediction
{
    [ColumnName("PredictedLabel")] // Ensure the PredictedSalePerformanceCategory property matches the label column name used in your ML.NET pipeline.
    public string PredictedSalePerformanceCategory;

    // The Score[] array contains the probabilities/confidences for each class, in the order of the label encoding used during training.
    [ColumnName("Score")]
    public float[] Score;
}