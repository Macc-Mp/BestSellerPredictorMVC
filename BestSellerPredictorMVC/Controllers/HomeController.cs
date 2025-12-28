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
            var webRoot = env?.WebRootPath ?? Path.Combine(contentRoot, "wwwroot");

            var preferred = Path.Combine(contentRoot, "wwwroot", "uploads");
            var alt1 = Path.Combine(contentRoot, "uploads");
            var alt2 = Path.Combine(webRoot, "uploads");

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
                _uploadPath = preferred;
                Directory.CreateDirectory(_uploadPath);
            }

            _logger.LogInformation("Upload path set to {UploadPath}", _uploadPath);
        }

        // Returns a per-session folder path under the configured uploads root.
        // Stores a stable folder id in session ("SessionFolderId") so subsequent requests use the same folder.
        private string GetSessionUploadFolder()
        {
            // Try read persisted session folder id
            var folderId = HttpContext.Session.GetString("SessionFolderId");
            if (string.IsNullOrEmpty(folderId))
            {
                // Prefer the session id if available
                folderId = HttpContext.Session.Id;
                if (string.IsNullOrEmpty(folderId))
                {
                    // Last resort: create a GUID and persist it into session
                    folderId = Guid.NewGuid().ToString("N");
                }
                HttpContext.Session.SetString("SessionFolderId", folderId);
            }

            var folder = Path.Combine(_uploadPath, folderId);
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            return folder;
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
            var id = Guid.NewGuid().ToString("N");
            var storedName = $"{id}_{originalFileName}";

            var sessionFolder = GetSessionUploadFolder();
            var filePath = Path.Combine(sessionFolder, storedName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await trainingExcelFile.CopyToAsync(stream);
            }

            _logger.LogInformation("Training file saved to {FilePath} (exists={Exists})", filePath, System.IO.File.Exists(filePath));

            HttpContext.Session.SetString("TrainingFile", storedName);
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
                    // keep previous mapping (QuantitySold used as numeric feature)
                    CurrentStock = td.QuantitySold,
                    SalePerformanceCategory = td.SalePerformanceCategory
                }).ToList();

                if (trainingData.Any())
                {
                    var modelFileName = $"{id}_MLModel.zip";
                    var expectedModelPath = Path.Combine(sessionFolder, modelFileName);

                    var trainer = new MLModelTrainer(expectedModelPath);
                    var (model, metrics) = trainer.TrainAndSaveModel(trainingData);

                    _logger.LogInformation("Trainer finished. Expected model path: {ModelPath}. Exists: {Exists}", expectedModelPath, System.IO.File.Exists(expectedModelPath));

                    // Robust model file detection limited to the session folder
                    string foundModelFileName = null;
                    if (model != null)
                    {
                        if (System.IO.File.Exists(expectedModelPath))
                        {
                            foundModelFileName = modelFileName;
                        }
                        else
                        {
                            try
                            {
                                var graceWindow = TimeSpan.FromSeconds(5);
                                var candidates = Directory.Exists(sessionFolder)
                                    ? Directory.GetFiles(sessionFolder, "*_MLModel.zip")
                                        .Select(p => new FileInfo(p))
                                        .Where(fi => fi.LastWriteTimeUtc >= DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(10))) // broad window
                                        .OrderByDescending(fi => fi.LastWriteTimeUtc)
                                        .ToArray()
                                    : Array.Empty<FileInfo>();

                                if (candidates.Length > 0)
                                {
                                    foundModelFileName = candidates[0].Name;
                                    _logger.LogInformation("Fallback model file chosen from session folder: {Fallback}", candidates[0].FullName);
                                }
                                else
                                {
                                    var plain = Path.Combine(sessionFolder, "MLModel.zip");
                                    if (System.IO.File.Exists(plain))
                                    {
                                        foundModelFileName = Path.GetFileName(plain);
                                        _logger.LogInformation("Fallback using plain MLModel.zip in session folder");
                                    }
                                }
                            }
                            catch (System.Exception ex)
                            {
                                _logger.LogError(ex, "Error while searching session folder for fallback model file");
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(foundModelFileName))
                    {
                        HttpContext.Session.SetString("ModelPath", foundModelFileName);
                        HttpContext.Session.SetString("ModelMetric_Micro", metrics?.MicroAccuracy.ToString("F4") ?? string.Empty);
                        HttpContext.Session.SetString("ModelMetric_Macro", metrics?.MacroAccuracy.ToString("F4") ?? string.Empty);
                        HttpContext.Session.SetString("ModelMetric_LogLoss", metrics?.LogLoss.ToString("F4") ?? string.Empty);
                        TempData["ModelTrained"] = true;

                        _logger.LogInformation("ModelPath session set to {ModelFile}", foundModelFileName);
                    }
                    else
                    {
                        _logger.LogWarning("Model was trained but no model file was found in session folder to set in session.");
                        TempData["ModelTrained"] = false;
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
        public async Task<IActionResult> UploadPredictionExcel(IFormFile predictionExcelFile)
        {
            // Diagnostics: log request-level info to show whether server saw multipart form and files
            try
            {
                _logger.LogInformation("UploadPredictionExcel called. Request ContentLength={ContentLength}, HasFormContentType={HasForm}, Method={Method}",
                    Request.ContentLength, Request.HasFormContentType, Request.Method);

                if (Request.HasFormContentType)
                {
                    _logger.LogInformation("Form keys: {Keys}", string.Join(",", Request.Form.Keys));
                    _logger.LogInformation("Form file count: {Count}", Request.Form.Files.Count);
                    for (int i = 0; i < Request.Form.Files.Count; i++)
                    {
                        var f = Request.Form.Files[i];
                        _logger.LogInformation("Form file {Index}: Name={Name}, FileName={FileName}, Length={Length}", i, f.Name, f.FileName, f.Length);
                    }
                }
                else
                {
                    _logger.LogWarning("Request does not have form content type; cannot read Request.Form.");
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error while inspecting request form");
            }

            // Diagnostic logs: confirm session + cookie + current session values
            _logger.LogInformation("UploadPredictionExcel called. Request Cookies: {Cookies}", Request.Headers["Cookie"].ToString());
            _logger.LogInformation("Session available: {IsAvailable}", HttpContext.Session.IsAvailable);
            _logger.LogInformation("Session before upload: ModelPath={ModelPath}, TrainingFile={TrainingFile}, PredictionFile={PredictionFile}",
                HttpContext.Session.GetString("ModelPath"),
                HttpContext.Session.GetString("TrainingFile"),
                HttpContext.Session.GetString("PredictionFile"));

            if (predictionExcelFile == null || predictionExcelFile.Length == 0)
            {
                TempData["PredictionExcelUploaded"] = false;
                _logger.LogWarning("predictionExcelFile is null or empty. Param null={IsNull} Length={Length}",
                    predictionExcelFile == null, predictionExcelFile?.Length);
                return RedirectToAction("Index");
            }

            var originalFileName = Path.GetFileName(predictionExcelFile.FileName);
            var id = Guid.NewGuid().ToString("N");
            var storedName = $"{id}_{originalFileName}";

            var sessionFolder = GetSessionUploadFolder();
            var filePath = Path.Combine(sessionFolder, storedName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await predictionExcelFile.CopyToAsync(stream);
            }

            _logger.LogInformation("Prediction file saved to {FilePath} (exists={Exists})", filePath, System.IO.File.Exists(filePath));

            HttpContext.Session.SetString("PredictionFile", storedName);
            TempData["PredictionExcelUploaded"] = true;

            _logger.LogInformation("Session after upload: ModelPath={ModelPath}, TrainingFile={TrainingFile}, PredictionFile={PredictionFile}",
                HttpContext.Session.GetString("ModelPath"),
                HttpContext.Session.GetString("TrainingFile"),
                HttpContext.Session.GetString("PredictionFile"));

            return RedirectToAction("Index");
        }

        // Diagnostic endpoint: returns cookie header, session state and session-folder uploads listing
        [HttpGet]
        public IActionResult SessionDebug()
        {
            var cookies = Request.Headers["Cookie"].ToString();
            var isAvailable = HttpContext.Session.IsAvailable;
            var modelPath = HttpContext.Session.GetString("ModelPath");
            var trainingFile = HttpContext.Session.GetString("TrainingFile");
            var predictionFile = HttpContext.Session.GetString("PredictionFile");

            List<object> files = new();
            string sessionFolder = GetSessionUploadFolder();
            try
            {
                if (Directory.Exists(sessionFolder))
                {
                    files = Directory.GetFiles(sessionFolder)
                        .Select(f => new { Name = Path.GetFileName(f), Size = new FileInfo(f).Length })
                        .Cast<object>()
                        .ToList();
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Error listing session uploads");
            }

            return Json(new
            {
                CookieHeader = cookies,
                SessionAvailable = isAvailable,
                ModelPath = modelPath,
                TrainingFile = trainingFile,
                PredictionFile = predictionFile,
                Uploads = files,
                UploadPath = sessionFolder
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

            var sessionFolder = GetSessionUploadFolder();

            if (!string.IsNullOrEmpty(trainingFile))
            {
                var trainingPath = Path.Combine(sessionFolder, trainingFile);
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
                var modelPath = Path.Combine(sessionFolder, modelFile);
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
                var predictionPath = Path.Combine(sessionFolder, predictionFile);
                if (System.IO.File.Exists(predictionPath))
                {
                    productList = loader.LoadData(predictionPath).ToList();
                    if (productList.Any())
                    {
                        var modelFullPath = Path.Combine(sessionFolder, HttpContext.Session.GetString("ModelPath")!);
                        var predictor = new MLModelPredictor(modelFullPath);
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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult ClearUploads()
        {
            try
            {
                var sessionFolder = GetSessionUploadFolder();
                if (Directory.Exists(sessionFolder))
                {
                    foreach (var file in Directory.GetFiles(sessionFolder))
                    {
                        try
                        {
                            System.IO.File.Delete(file);
                            _logger.LogInformation("Deleted session upload file {File}", file);
                        }
                        catch (Exception exFile)
                        {
                            _logger.LogError(exFile, "Error deleting file {File}", file);
                        }
                    }

                    try
                    {
                        Directory.Delete(sessionFolder, recursive: true);
                    }
                    catch { /* non-fatal */ }
                }

                HttpContext.Session.Remove("TrainingFile");
                HttpContext.Session.Remove("PredictionFile");
                HttpContext.Session.Remove("ModelPath");
                HttpContext.Session.Remove("ModelMetric_Micro");
                HttpContext.Session.Remove("ModelMetric_Macro");
                HttpContext.Session.Remove("ModelMetric_LogLoss");

                TempData["UploadsCleared"] = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing uploads for session");
                TempData["UploadsCleared"] = false;
            }

            return RedirectToAction("Index");
        }

        public IActionResult Privacy() => View();

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
