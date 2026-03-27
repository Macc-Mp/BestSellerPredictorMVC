// DataModels/ProductSalePrediction.cs
using Microsoft.ML.Data; // Add this using statement

namespace BestSellerPredictorMVC.Models
{
    public class ProductSalePrediction
    {
    [ColumnName("PredictedLabel")]
    public string? PredictedSalePerformanceCategory { get; set; }

    // The Score[] array contains the probabilities/confidences for each class, in the order of the label encoding used during training.
    [ColumnName("Score")]
    public float[]? Score { get; set; }

    // Optional: human-readable labels that correspond to Score[] indices. Populated by the predictor when available.
        public string[]? ScoreLabels { get; set; }
    }
}
    