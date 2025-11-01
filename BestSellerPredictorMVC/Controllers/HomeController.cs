using System.Diagnostics;
using BestSellerPredictorMVC.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using System.IO;
//check github
namespace BestSellerPredictorMVC.Controllers
{
    public class HomeController : Controller
    {
        private readonly SqlServerDataService _service;
        private readonly ILogger<HomeController> _logger;
        private readonly string _uploadPath = Path.Combine("wwwroot", "uploads");
        private readonly string _uploadedFileName = "uploaded_training.xlsx";

        public HomeController(SqlServerDataService service, ILogger<HomeController> logger)
        {
            _service = service;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> UploadExcel(IFormFile excelFile)
        {
            if (excelFile != null && excelFile.Length > 0)
            {
                Directory.CreateDirectory(_uploadPath);
                var filePath = Path.Combine(_uploadPath, _uploadedFileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await excelFile.CopyToAsync(stream);
                }
                TempData["ExcelFileUploaded"] = true;
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult RemoveExcel()
        {
            var filePath = Path.Combine(_uploadPath, _uploadedFileName);
            if (System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
            }
            TempData["ExcelFileUploaded"] = false;
            return RedirectToAction("Index");
        }

        public IActionResult Index()
        {
            var filePath = Path.Combine(_uploadPath, _uploadedFileName);
            if (System.IO.File.Exists(filePath))
            {
                ViewBag.ExcelFileUploaded = true;
                var loader = new ExcelDataLoader();
                var trainingDataExcel = loader.LoadTrainingData(filePath).ToList();
                var trainingData = trainingDataExcel.Select(td => new ProductSalesData
                {
                    ProductId = td.ProductId,
                    ProductName = td.ProductName,
                    Category = td.Category,
                    UnitPrice = td.UnitPrice,
                    CurrentStock = td.QuantitySold,
                    SalePerformanceCategory = td.SalePerformanceCategory
                }).ToList();

                var products = _service.GetProductsForPrediction().ToList();

                var modelPath = "C:\\Users\\paule\\Documents\\Cpp_Test\\BestSellerPredictorMVC\\BestSellerPredictorMVC\\MLModel.zip";
                var trainer = new MLModelTrainer(modelPath);
                var (model, metrics) = trainer.TrainAndSaveModel(trainingData);

                var predictor = new MLModelPredictor(modelPath);
                var predictions = predictor.PredictBatch(products).ToList();

                var viewModel = new IndexViewModel
                {
                    TrainingDataList = trainingDataExcel,
                    ProductList = products,
                    PredictionResults = predictions,
                    ModelEvalMetrics = metrics
                };

                return View(viewModel);
            }
            else
            {
                ViewBag.ExcelFileUploaded = false;
                // Set all lists to empty!
                var viewModel = new IndexViewModel
                {
                    TrainingDataList = new List<TrainingDataExcel>(),
                    ProductList = new List<ProductSalesData>(),
                    PredictionResults = new List<ProductSalePrediction>(),
                    ModelEvalMetrics = null
                };
                return View(viewModel);
            }
        }

        public IActionResult Privacy() => View();

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
