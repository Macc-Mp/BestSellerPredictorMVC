using System.Diagnostics;
using BestSellerPredictorMVC.Models;
using BestSellerPredictorMVC.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;

namespace BestSellerPredictorMVC.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly string _uploadPath;
        private readonly ModelStore _modelStore;

        public HomeController(ILogger<HomeController> logger, IWebHostEnvironment env, ModelStore modelStore)
        {
            _logger = logger;
            _modelStore = modelStore;

            var webRoot = env?.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            _uploadPath = Path.Combine(webRoot, "uploads");
            Directory.CreateDirectory(_uploadPath);
        }

        [HttpPost]
        public async Task<IActionResult> UploadTrainingExcel(IFormFile trainingExcelFile)
        {
            if (trainingExcelFile == null || trainingExcelFile.Length == 0)
            {
                TempData["TrainingExcelUploaded"] = false;
                return RedirectToAction("Index");
            }

            var originalFileName = Path.GetFileName(trainingExcelFile.FileName);
            var id = Guid.NewGuid().ToString("N"); // token
            var storedName = $"{id}_{originalFileName}";
            var filePath = Path.Combine(_uploadPath, storedName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await trainingExcelFile.CopyToAsync(stream);
            }

            TempData["TrainingExcelUploaded"] = true;

            try
            {
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

                if (trainingData.Any())
                {
                    var modelFileName = $"{id}_MLModel.zip";
                    var modelPath = Path.Combine(_uploadPath, modelFileName);

                    var trainer = new MLModelTrainer(modelPath);
                    var (model, metrics) = trainer.TrainAndSaveModel(trainingData);

                    if (System.IO.File.Exists(modelPath))
                    {
                        var rec = new ModelRecord
                        {
                            Token = id,
                            ModelFileName = modelFileName,
                            TrainingFileName = storedName,
                            ModelMetric_Micro = metrics?.MicroAccuracy.ToString("F4") ?? string.Empty,
                            ModelMetric_Macro = metrics?.MacroAccuracy.ToString("F4") ?? string.Empty,
                            ModelMetric_LogLoss = metrics?.LogLoss.ToString("F4") ?? string.Empty
                        };
                        await _modelStore.SaveAsync(rec);

                        // Redirect with token in query string so Index knows about this trained model
                        return RedirectToAction("Index", new { token = id });
                    }
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Training failed after upload");
                TempData["ModelTrained"] = false;
            }

            return RedirectToAction("Index");
        }
            
        [HttpPost]
        public async Task<IActionResult> UploadPredictionExcel(IFormFile predictionExcelFile, string token)
        {
            if (predictionExcelFile == null || predictionExcelFile.Length == 0 || string.IsNullOrWhiteSpace(token))
            {
                TempData["PredictionExcelUploaded"] = false;
                return RedirectToAction("Index", new { token });
            }

            var originalFileName = Path.GetFileName(predictionExcelFile.FileName);
            var id = Guid.NewGuid().ToString("N");
            var storedName = $"{id}_{originalFileName}";
            var filePath = Path.Combine(_uploadPath, storedName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await predictionExcelFile.CopyToAsync(stream);
            }

            var rec = await _modelStore.GetAsync(token);
            if (rec != null)
            {
                rec.PredictionFileName = storedName;
                await _modelStore.SaveAsync(rec);
                TempData["PredictionExcelUploaded"] = true;
            }
            else
            {
                TempData["PredictionExcelUploaded"] = false;
            }

            return RedirectToAction("Index", new { token });
        }

        public async Task<IActionResult> Index(string token)
        {
            var loader = new ExcelDataLoader();

            ViewBag.ModelToken = token;

            var trainingDataExcel = new List<TrainingDataExcel>();
            var productList = new List<ProductSalesData>();
            var predictions = new List<ProductSalePrediction>();

            if (!string.IsNullOrWhiteSpace(token))
            {
                var rec = await _modelStore.GetAsync(token);
                if (rec != null)
                {
                    var trainingPath = Path.Combine(_uploadPath, rec.TrainingFileName);
                    if (System.IO.File.Exists(trainingPath))
                    {
                        trainingDataExcel = loader.LoadTrainingData(trainingPath).ToList();
                        ViewBag.TrainingExcelUploaded = true;
                    }

                    var modelPath = Path.Combine(_uploadPath, rec.ModelFileName);
                    if (System.IO.File.Exists(modelPath))
                    {
                        ViewBag.ModelTrained = true;
                        ViewBag.ModelMetric_Micro = rec.ModelMetric_Micro;
                        ViewBag.ModelMetric_Macro = rec.ModelMetric_Macro;
                        ViewBag.ModelMetric_LogLoss = rec.ModelMetric_LogLoss;
                    }

                    if (!string.IsNullOrWhiteSpace(rec.PredictionFileName))
                    {
                        var predictionPath = Path.Combine(_uploadPath, rec.PredictionFileName);
                        if (System.IO.File.Exists(predictionPath))
                        {
                            productList = loader.LoadData(predictionPath).ToList();
                            if (productList.Any())
                            {
                                var predictor = new MLModelPredictor(modelPath);
                                predictions = predictor.PredictBatch(productList).ToList();
                                ViewBag.PredictionExcelUploaded = true;
                            }
                        }
                    }
                }
            }

            var viewModel = new IndexViewModel
            {
                TrainingDataList = trainingDataExcel,
                ProductList = productList,
                PredictionResults = predictions,
                ModelEvalMetrics = null
            };

            return View(viewModel);
        }

        public IActionResult Privacy() => View();

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
