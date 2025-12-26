using System.Diagnostics;
using BestSellerPredictorMVC.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using System.Threading.Tasks;

namespace BestSellerPredictorMVC.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly string _uploadPath;

        public HomeController(ILogger<HomeController> logger, IWebHostEnvironment env)
        {
            _logger = logger;

            // Resolve a canonical uploads folder. Prefer site\wwwroot\uploads but fall back to other sensible locations.
            var contentRoot = env?.ContentRootPath ?? Directory.GetCurrentDirectory();
            var webRoot = env?.WebRootPath ?? Path.Combine(contentRoot, "wwwroot");

            var preferred = Path.Combine(contentRoot, "wwwroot", "uploads");   // typical
            var alt1 = Path.Combine(contentRoot, "uploads");                  // alternative
            var alt2 = Path.Combine(webRoot, "uploads");                      // fallback using WebRootPath

            // Choose an existing candidate if present, otherwise create the preferred path
            if (Directory.Exists(preferred))
            {
                _uploadPath = preferred;
            }
            else if (Directory.Exists(alt1))
            {
                _uploadPath = alt1;
            }
            else if (Directory.Exists(alt2))
            {
                _uploadPath = alt2;
            }
            else
            {
                // create the preferred one under site\wwwroot\uploads
                _uploadPath = preferred;
                Directory.CreateDirectory(_uploadPath);
            }

            _logger.LogInformation("Upload path set to {UploadPath}", _uploadPath);
        }

        // Upload training file, save with unique name, train immediately and store model filename + metrics in session
        [HttpPost]
        public async Task<IActionResult> UploadTrainingExcel(IFormFile trainingExcelFile)
        {
            if (trainingExcelFile == null || trainingExcelFile.Length == 0)
            {
                TempData["TrainingExcelUploaded"] = false;
                return RedirectToAction("Index");
            }

            var originalFileName = Path.GetFileName(trainingExcelFile.FileName);
            var id = Guid.NewGuid().ToString("N");
            var storedName = $"{id}_{originalFileName}";
            var filePath = Path.Combine(_uploadPath, storedName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await trainingExcelFile.CopyToAsync(stream);
            }

            _logger.LogInformation("Training file saved to {FilePath} (exists={Exists})", filePath, System.IO.File.Exists(filePath));

            // persist in session
            HttpContext.Session.SetString("TrainingFile", storedName);
            TempData["TrainingExcelUploaded"] = true;

            // Train immediately for this session and save model with same prefix
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

                    _logger.LogInformation("Trainer finished. Model file exists: {Exists}", System.IO.File.Exists(modelPath));

                    if (model != null && System.IO.File.Exists(modelPath))
                    {
                        HttpContext.Session.SetString("ModelPath", modelFileName);
                        HttpContext.Session.SetString("ModelMetric_Micro", metrics?.MicroAccuracy.ToString("F4") ?? string.Empty);
                        HttpContext.Session.SetString("ModelMetric_Macro", metrics?.MacroAccuracy.ToString("F4") ?? string.Empty);
                        HttpContext.Session.SetString("ModelMetric_LogLoss", metrics?.LogLoss.ToString("F4") ?? string.Empty);
                        TempData["ModelTrained"] = true;
                    }
                }
            }
            catch (System.Exception ex)
            {
                // log but don't crash the request
                _logger.LogError(ex, "Training failed after upload");
                TempData["ModelTrained"] = false;
            }

            return RedirectToAction("Index");
        }

        // Upload prediction file, save with unique name and persist in session
        [HttpPost]
        public async Task<IActionResult> UploadPredictionExcel(IFormFile predictionExcelFile)
        {
            if (predictionExcelFile == null || predictionExcelFile.Length == 0)
            {
                TempData["PredictionExcelUploaded"] = false;
                return RedirectToAction("Index");
            }

            var originalFileName = Path.GetFileName(predictionExcelFile.FileName);
            var id = Guid.NewGuid().ToString("N");
            var storedName = $"{id}_{originalFileName}";
            var filePath = Path.Combine(_uploadPath, storedName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await predictionExcelFile.CopyToAsync(stream);
            }

            _logger.LogInformation("Prediction file saved to {FilePath} (exists={Exists})", filePath, System.IO.File.Exists(filePath));

            HttpContext.Session.SetString("PredictionFile", storedName);
            TempData["PredictionExcelUploaded"] = true;

            return RedirectToAction("Index");
        }

        public IActionResult Index()
        {
            var loader = new ExcelDataLoader();

            // Read session keys (per-user)
            var trainingFile = HttpContext.Session.GetString("TrainingFile");
            var predictionFile = HttpContext.Session.GetString("PredictionFile");
            var modelFile = HttpContext.Session.GetString("ModelPath");

            var trainingDataExcel = new List<TrainingDataExcel>();
            var productList = new List<ProductSalesData>();
            var predictions = new List<ProductSalePrediction>();

            // Only load training data when the session has a training file and file exists on disk
            if (!string.IsNullOrEmpty(trainingFile))
            {
                var trainingPath = Path.Combine(_uploadPath, trainingFile);
                if (System.IO.File.Exists(trainingPath))
                {
                    trainingDataExcel = loader.LoadTrainingData(trainingPath).ToList();
                    ViewBag.TrainingExcelUploaded = true;
                }
                else
                {
                    ViewBag.TrainingExcelUploaded = false;
                }
            }
            else
            {
                ViewBag.TrainingExcelUploaded = false;
            }

            // Model trained for this session?
            if (!string.IsNullOrEmpty(modelFile))
            {
                var modelPath = Path.Combine(_uploadPath, modelFile);
                if (System.IO.File.Exists(modelPath))
                {
                    ViewBag.ModelTrained = true;
                    // expose metrics stored in session (strings)
                    ViewBag.ModelMetric_Micro = HttpContext.Session.GetString("ModelMetric_Micro");
                    ViewBag.ModelMetric_Macro = HttpContext.Session.GetString("ModelMetric_Macro");
                    ViewBag.ModelMetric_LogLoss = HttpContext.Session.GetString("ModelMetric_LogLoss");
                }
                else
                {
                    ViewBag.ModelTrained = false;
                }
            }
            else
            {
                ViewBag.ModelTrained = false;
            }

            // Only run predictions when the session has prediction file AND a model for the session exists
            if (!string.IsNullOrEmpty(predictionFile) && ViewBag.ModelTrained == true)
            {
                var predictionPath = Path.Combine(_uploadPath, predictionFile);
                if (System.IO.File.Exists(predictionPath))
                {
                    productList = loader.LoadData(predictionPath).ToList();
                    if (productList.Any())
                    {
                        var predictor = new MLModelPredictor(Path.Combine(_uploadPath, HttpContext.Session.GetString("ModelPath")!));
                        predictions = predictor.PredictBatch(productList).ToList();
                        ViewBag.PredictionExcelUploaded = true;
                    }
                }
                else
                {
                    ViewBag.PredictionExcelUploaded = false;
                }
            }
            else
            {
                ViewBag.PredictionExcelUploaded = false;
            }

            var viewModel = new IndexViewModel
            {
                TrainingDataList = trainingDataExcel,
                ProductList = productList,
                PredictionResults = predictions,
                // Keep null: metrics shown via ViewBag to avoid reconstructing ML types
                ModelEvalMetrics = null
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
