using System.Diagnostics;
using BestSellerPredictorMVC.Models;
using Microsoft.AspNetCore.Mvc;

namespace BestSellerPredictorMVC.Controllers
{
    public class HomeController : Controller
    {
        private readonly SqlServerDataService _service;
        private readonly ILogger<HomeController> _logger;

        public HomeController(SqlServerDataService service, ILogger<HomeController> logger)
        {
            _service = service;
            _logger = logger;
        }

        public IActionResult Index()
        {
            var excelLoader = new ExcelDataLoader();
            var trainingData = excelLoader.LoadTrainingData("C:\\Users\\paule\\Documents\\Cpp_Test\\BestSellerPredictorMVC\\BestSellerPredictorMVC\\HistoricalProductSales.xlsx").ToList();
            var products = _service.GetProductsForPrediction().ToList();

            var modelPath = "C:\\Users\\paule\\Documents\\Cpp_Test\\BestSellerPredictorMVC\\BestSellerPredictorMVC\\MLModel.zip";
            var trainer = new MLModelTrainer(modelPath);
            var (model, metrics) = trainer.TrainAndSaveModel(trainingData.Select(td => new ProductSalesData
            {
                ProductId = td.ProductId,
                ProductName = td.ProductName,
                Category = td.Category,
                UnitPrice = td.UnitPrice,
                CurrentStock = td.QuantitySold,
                SalePerformanceCategory = td.SalePerformanceCategory
            }));

            var predictor = new MLModelPredictor(modelPath);
            var predictions = predictor.PredictBatch(products).ToList();

            var viewModel = new IndexViewModel
            {
                TrainingDataList = trainingData,
                ProductList = products,
                PredictionResults = predictions,
                ModelEvalMetrics = metrics
            };

            return View(viewModel);
        }

        public IActionResult Privacy() => View();

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
