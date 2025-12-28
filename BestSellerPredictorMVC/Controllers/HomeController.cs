using System;
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

            var contentRoot = env?.ContentRootPath ?? Directory.GetCurrentDirectory();

            // Prefer the actual web root; if it's not set, use contentRoot + "wwwroot"
            var webRoot = env?.WebRootPath;
            if (string.IsNullOrEmpty(webRoot))
            {
                webRoot = Path.Combine(contentRoot, "wwwroot");
            }

            // Normalize accidental duplication like "...\\wwwroot\\wwwroot" -> keep single "wwwroot"
            try
            {
                var duplicateSegment = Path.Combine("wwwroot", "wwwroot");
                var idx = webRoot.IndexOf(duplicateSegment, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    // Keep up to the first "wwwroot"
                    var firstIndex = webRoot.IndexOf("wwwroot", StringComparison.OrdinalIgnoreCase);
                    if (firstIndex >= 0)
                    {
                        webRoot = webRoot.Substring(0, firstIndex + "wwwroot".Length);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error normalizing webRoot path; using value as-is: {WebRoot}", webRoot);
            }

            var uploadDir = Path.Combine(webRoot, "uploads");

            if (!Directory.Exists(uploadDir))
            {
                Directory.CreateDirectory(uploadDir);
            }

            _uploadPath = uploadDir;

            _logger.LogInformation("HomeController initialized. ContentRoot={ContentRoot} WebRoot={WebRoot} UploadPath={UploadPath}",
                contentRoot, webRoot, _uploadPath);
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
            // Use a per-file GUID instead of relying on Session.Id (more robust)
            var fileGuid = Guid.NewGuid().ToString("N");
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var storedName = $"{fileGuid}_{timestamp}_{originalFileName}";
            var filePath = Path.Combine(_uploadPath, storedName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await trainingExcelFile.CopyToAsync(stream);
            }

            _logger.LogInformation("Training file saved to {FilePath} as {StoredName} (exists={Exists})", filePath, storedName, System.IO.File.Exists(filePath));

            HttpContext.Session.SetString("TrainingFile", storedName);
            TempData["TrainingExcelUploaded"] = true;

            try
            {
                await HttpContext.Session.CommitAsync();
                _logger.LogInformation("Session committed after setting TrainingFile={TrainingFile}", storedName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to commit session after setting TrainingFile");
            }

            try
            {
                var loader = new ExcelDataLoader();
                var trainingDataExcel = loader.LoadTrainingData(filePath).ToList();
                _logger.LogInformation("Loaded training excel rows: {Count}", trainingDataExcel.Count);

                var trainingData = trainingDataExcel.Select(td => new ProductSalesData
                {
                    ProductId = td.ProductId,
                    ProductName = td.ProductName,
                    Category = td.Category,
                    UnitPrice = td.UnitPrice,
                    CurrentStock = td.QuantitySold,
                    SalePerformanceCategory = td.SalePerformanceCategory
                }).ToList();

                _logger.LogInformation("Mapped training rows to ProductSalesData: {Count}", trainingData.Count);

                if (trainingData.Any())
                {
                    var modelFileName = $"{fileGuid}_{timestamp}_MLModel.zip";
                    var modelPath = Path.Combine(_uploadPath, modelFileName);

                    // Pass controller logger into trainer so ML logs go to App Service logs / App Insights
                    var trainer = new MLModelTrainer(modelPath, _logger);
                    var (model, metrics) = trainer.TrainAndSaveModel(trainingData);

                    _logger.LogInformation("Trainer finished. Expected modelPath on disk: {ModelPath}", modelPath);
                    _logger.LogInformation("Model file exists at expected path: {Exists}", System.IO.File.Exists(modelPath));

                    if (model != null && System.IO.File.Exists(modelPath))
                    {
                        HttpContext.Session.SetString("ModelPath", modelFileName);
                        HttpContext.Session.SetString("ModelMetric_Micro", metrics?.MicroAccuracy.ToString("F4") ?? string.Empty);
                        HttpContext.Session.SetString("ModelMetric_Macro", metrics?.MacroAccuracy.ToString("F4") ?? string.Empty);
                        HttpContext.Session.SetString("ModelMetric_LogLoss", metrics?.LogLoss.ToString("F4") ?? string.Empty);
                        TempData["ModelTrained"] = true;

                        try
                        {
                            await HttpContext.Session.CommitAsync();
                            _logger.LogInformation("Session committed after setting ModelPath={ModelPath}", modelFileName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to commit session after setting ModelPath");
                        }
                    }
                    else
                    {
                        TempData["ModelTrained"] = false;
                        TempData["TrainingError"] = "Model training did not produce a saved model file. Check server logs (label count, exceptions).";
                        _logger.LogWarning("Model not set in session because model==null or file not found at {ExpectedPath}", modelPath);
                    }
                }
                else
                {
                    TempData["ModelTrained"] = false;
                    _logger.LogWarning("No training data rows after mapping; skipping training.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Training failed after upload. Exception: {Message}", ex.Message);
                TempData["ModelTrained"] = false;
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        public async Task<IActionResult> UploadPredictionExcel(IFormFile predictionExcelFile)
        {
            _logger.LogInformation("UploadPredictionExcel called. Request Cookies: {Cookies}", Request.Headers["Cookie"].ToString());
            _logger.LogInformation("Session available: {IsAvailable}", HttpContext.Session.IsAvailable);
            _logger.LogInformation("Session before upload: ModelPath={ModelPath}, TrainingFile={TrainingFile}, PredictionFile={PredictionFile}",
                HttpContext.Session.GetString("ModelPath"),
                HttpContext.Session.GetString("TrainingFile"),
                HttpContext.Session.GetString("PredictionFile"));

            if (predictionExcelFile == null || predictionExcelFile.Length == 0)
            {
                TempData["PredictionExcelUploaded"] = false;
                return RedirectToAction("Index");
            }

            var originalFileName = Path.GetFileName(predictionExcelFile.FileName);
            var fileGuid = Guid.NewGuid().ToString("N");
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var storedName = $"{fileGuid}_{timestamp}_{originalFileName}";
            var filePath = Path.Combine(_uploadPath, storedName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await predictionExcelFile.CopyToAsync(stream);
            }

            _logger.LogInformation("Prediction file saved to {FilePath} as {StoredName} (exists={Exists})", filePath, storedName, System.IO.File.Exists(filePath));

            HttpContext.Session.SetString("PredictionFile", storedName);
            TempData["PredictionExcelUploaded"] = true;

            try
            {
                await HttpContext.Session.CommitAsync();
                _logger.LogInformation("Session committed after setting PredictionFile={PredictionFile}", storedName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to commit session after setting PredictionFile");
            }

            _logger.LogInformation("Session after upload: ModelPath={ModelPath}, TrainingFile={TrainingFile}, PredictionFile={PredictionFile}",
                HttpContext.Session.GetString("ModelPath"),
                HttpContext.Session.GetString("TrainingFile"),
                HttpContext.Session.GetString("PredictionFile"));

            return RedirectToAction("Index");
        }

        // Diagnostic endpoint: returns cookie header, session state and uploads folder listing
        [HttpGet]
        public IActionResult SessionDebug()
        {
            var cookies = Request.Headers["Cookie"].ToString();
            var isAvailable = HttpContext.Session.IsAvailable;
            var modelPath = HttpContext.Session.GetString("ModelPath");
            var trainingFile = HttpContext.Session.GetString("TrainingFile");
            var predictionFile = HttpContext.Session.GetString("PredictionFile");

            List<object> files = new();
            try
            {
                if (Directory.Exists(_uploadPath))
                {
                    files = Directory.GetFiles(_uploadPath)
                        .Select(f => new { Name = Path.GetFileName(f), Size = new FileInfo(f).Length })
                        .Cast<object>()
                        .ToList();
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error listing uploads");
            }

            return Json(new
            {
                CookieHeader = cookies,
                SessionAvailable = isAvailable,
                ModelPath = modelPath,
                TrainingFile = trainingFile,
                PredictionFile = predictionFile,
                Uploads = files,
                UploadPath = _uploadPath
            });
        }

        public IActionResult Index()
        {
            var loader = new ExcelDataLoader();

            var trainingFile = HttpContext.Session.GetString("TrainingFile");
            var predictionFile = HttpContext.Session.GetString("PredictionFile");
            var modelFile = HttpContext.Session.GetString("ModelPath");

            _logger.LogInformation("Index: Session keys ModelPath={ModelPath} TrainingFile={TrainingFile} PredictionFile={PredictionFile}",
                modelFile, trainingFile, predictionFile);

            var trainingDataExcel = new List<TrainingDataExcel>();
            var productList = new List<ProductSalesData>();
            var predictions = new List<ProductSalePrediction>();

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

            if (!string.IsNullOrEmpty(modelFile))
            {
                var modelPath = Path.Combine(_uploadPath, modelFile);
                if (System.IO.File.Exists(modelPath))
                {
                    ViewBag.ModelTrained = true;
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
