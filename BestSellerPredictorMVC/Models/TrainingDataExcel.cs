using Microsoft.ML.Data; // Add this using statement

namespace BestSellerPredictorMVC.Models
{
    public class TrainingDataExcel
    {

        [LoadColumn(1)]
        required public string ProductId { get; set; }

        [LoadColumn(2)]
        required public string ProductName { get; set; }

        [LoadColumn(3)]
        required public string Category { get; set; }

        [LoadColumn(4)]
        required public float UnitPrice { get; set; }

        [LoadColumn(5)]
        required public int QuantitySold { get; set; }

        [LoadColumn(6)]

        required public string SalePerformanceCategory { get; set; }

    }
}
