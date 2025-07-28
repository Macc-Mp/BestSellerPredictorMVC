// DataModels/ProductSalesData.cs
using Microsoft.ML.Data; // Add this using statement

namespace BestSellerPredictorMVC.Models
{
    public class ProductSalesData
    {
        // Adjust LoadColumn indices based on your HistoricalProductSales.xlsx column order
        // Example:
        // ProductId is in column A (index 0)
        // ProductName is in column B (index 1)
        // Category is in column C (index 2)
        // UnitPrice is in column D (index 3)
        // QuantitySold is in column E (index 4)
        // SalePerformanceCategory is in column F (index 5)
        [LoadColumn(0)]
        public int ProductId { get; set; }

        [LoadColumn(1)]
        required public string ProductName { get; set; }

        [LoadColumn(2)]
        required public string Category { get; set; }

        [LoadColumn(3)]
        public float UnitPrice { get; set; }

        [LoadColumn(4)]
        public float CurrentStock { get; set; } // This is a key feature for your model

        [LoadColumn(5)] // This is the LABEL for training
        required public string SalePerformanceCategory { get; set; }
    }
}