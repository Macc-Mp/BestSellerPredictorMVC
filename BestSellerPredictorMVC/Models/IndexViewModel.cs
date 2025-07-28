using Microsoft.ML.Data;
using System.Collections.Generic;

namespace BestSellerPredictorMVC.Models
{
    public class IndexViewModel
    {
        public List<ProductSalesData> ProductList { get; set; } = new();
        public List<TrainingDataExcel> TrainingDataList { get; set;} = new();
        
        public MulticlassClassificationMetrics? ModelEvalMetrics { get; set; }
        
        public List<ProductSalePrediction> PredictionResults { get; set; } = new(); // Add this
    }
}