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
using BestSellerPredictorMVC.Services;

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

            // Determine content root
            var contentRoot = env?.ContentRootPath ?? Directory.GetCurrentDirectory();

            // Get a candidate webRoot from IWebHostEnvironment
            var webRoot = env?.WebRootPath;

            // If IWebHostEnvironment.WebRootPath is missing or not present on disk, try the common App Service location
            if (string.IsNullOrEmpty(webRoot) || !Directory.Exists(webRoot))
            {
                var home = Environment.GetEnvironmentVariable("HOME");
                if (!string.IsNullOrEmpty(home))
                {
                    var appServiceWebRoot = Path.Combine(home, "site", "wwwroot");
                    if (Directory.Exists(appServiceWebRoot))
                    {
                        webRoot = appServiceWebRoot;
                        _logger.LogInformation("Using App Service webroot candidate: {WebRoot}", webRoot);
                    }
                }
            }

            // Final fallback to contentRoot/wwwroot
            if (string.IsNullOrEmpty(webRoot) || !Directory.Exists(webRoot))
            {
                webRoot = Path.Combine(contentRoot, "wwwroot");
                _logger.LogWarning("Falling back to contentRoot/wwwroot: {WebRoot}", webRoot);
            }

            // Normalize accidental duplication like "...\\wwwroot\\wwwroot" -> keep single "wwwroot"
            try
            {
                var duplicateSegment = Path.Combine("wwwroot", "wwwroot");
                var idx = webRoot.IndexOf(duplicateSegment, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var firstIndex = webRoot.IndexOf("wwwroot", StringComparison.OrdinalIgnoreCase);
                    if (firstIndex >= 0)
                    {
                        webRoot = webRoot.Substring(0, firstIndex + "wwwroot".Length);
                        _logger.LogInformation("Normalized duplicated webroot to: {WebRoot}", webRoot);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error normalizing webRoot path; using value as-is: {WebRoot}", webRoot);
            }

            // Build uploads path. Also handle the case where files are actually under webRoot\wwwroot (nested)
            var uploadDir = Path.Combine(webRoot, "uploads");
            if (!Directory.Exists(uploadDir))
            {
                // Check common nested location: webRoot\wwwroot\uploads
                var nested = Path.Combine(webRoot, "wwwroot", "uploads");
                if (Directory.Exists(nested))
                {
                    uploadDir = nested;
                    _logger.LogInformation("Using nested uploads path: {UploadDir}", uploadDir);
                }
                else
                {
                    try
                    {
                        Directory.CreateDirectory(uploadDir);
                        _logger.LogInformation("Created uploads directory: {UploadDir}", uploadDir);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to create uploads directory at {UploadDir}. Falling back to content root uploads.", uploadDir);
                        // Fallback: contentRoot/wwwroot/uploads
                        var fallback = Path.Combine(contentRoot, "wwwroot", "uploads");
                        Directory.CreateDirectory(fallback);
                        uploadDir = fallback;
                    }
                }
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

            // Generate a unique token for this user's model/session
            var token = Guid.NewGuid().ToString("N");

            var originalFileName = Path.GetFileName(trainingExcelFile.FileName);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var storedName = $"{token}_{timestamp}_{originalFileName}";
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
                    // Save model info using ModelStore
                    var modelFileName = $"{token}_{timestamp}_MLModel.zip";
                    var modelPath = Path.Combine(_uploadPath, modelFileName);

                    // Pass controller logger into trainer so ML logs go to App Service logs / App Insights
                    var trainer = new MLModelTrainer(modelPath, _logger);
                    var (model, metrics) = trainer.TrainAndSaveModel(trainingData);

                    _logger.LogInformation("Trainer finished. Expected modelPath on disk: {ModelPath}", modelPath);
                    _logger.LogInformation("Model file exists at expected path: {Exists}", System.IO.File.Exists(modelPath));

                    if (model != null && System.IO.File.Exists(modelPath))
                    {
                        HttpContext.Session.SetString("ModelPath", modelFileName);
                        // Persist token in session so we can correlate later (predictions, downloads, debug)
                        HttpContext.Session.SetString("ModelToken", token);
                        HttpContext.Session.SetString("ModelMetric_Micro", metrics?.MicroAccuracy.ToString("F4") ?? string.Empty);
                        HttpContext.Session.SetString("ModelMetric_Macro", metrics?.MacroAccuracy.ToString("F4") ?? string.Empty);
                        HttpContext.Session.SetString("ModelMetric_LogLoss", metrics?.LogLoss.ToString("F4") ?? string.Empty);
                        TempData["ModelTrained"] = true;
                        // Expose token to the UI so user can copy it if needed
                        TempData["ModelToken"] = token;

                        try
                        {
                            await HttpContext.Session.CommitAsync();
                            _logger.LogInformation("Session committed after setting ModelPath={ModelPath} and ModelToken={Token}", modelFileName, token);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to commit session after setting ModelPath/ModelToken");
                        }

                        // Save a persistent model record (file-backed, free-tier friendly)
                        try
                        {
                            var record = new ModelRecord
                            {
                                Token = token,
                                ModelFileName = modelFileName,
                                TrainingFileName = storedName,
                                ModelMetric_Micro = HttpContext.Session.GetString("ModelMetric_Micro") ?? string.Empty,
                                ModelMetric_Macro = HttpContext.Session.GetString("ModelMetric_Macro") ?? string.Empty,
                                ModelMetric_LogLoss = HttpContext.Session.GetString("ModelMetric_LogLoss") ?? string.Empty,
                                CreatedUtc = DateTime.UtcNow
                            };

                            await _modelStore.SaveAsync(record);
                            _logger.LogInformation("Saved model record for token {Token}", token);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to save model record for token {Token}", token);
                        }
                    }
                    else
                    {
                        TempData["ModelTrained"] = false;
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
            _logger.LogInformation("UploadPredictionExcel called. SessionId={SessionId} Cookies={Cookies} SessionAvailable={IsAvailable} ModelPath(before)={ModelPath} TrainingFile={TrainingFile}",
                HttpContext.Session?.Id,
                Request.Headers["Cookie"].ToString(),
                HttpContext.Session.IsAvailable,
                HttpContext.Session.GetString("ModelPath"),
                HttpContext.Session.GetString("TrainingFile"));

            // Try to ensure ModelPath exists in session before saving prediction:
            var modelPath = HttpContext.Session.GetString("ModelPath");
            if (string.IsNullOrEmpty(modelPath))
            {
                // Attempt token-based rehydrate (TempData, form or query) - existing behavior
                string token = null;
                try { token ??= TempData.Peek("ModelToken") as string; } catch { }
                if (string.IsNullOrEmpty(token) && Request.HasFormContentType && Request.Form.ContainsKey("token"))
                    token = Request.Form["token"].ToString();
                if (string.IsNullOrEmpty(token) && Request.Query.ContainsKey("token"))
                    token = Request.Query["token"].ToString();

                if (!string.IsNullOrEmpty(token))
                {
                    try
                    {
                        var record = await _modelStore.GetAsync(token.Trim());
                        if (record != null && !string.IsNullOrEmpty(record.ModelFileName))
                        {
                            var expectedModelPath = Path.Combine(_uploadPath, record.ModelFileName);
                            if (System.IO.File.Exists(expectedModelPath))
                            {
                                HttpContext.Session.SetString("ModelPath", record.ModelFileName);
                                HttpContext.Session.SetString("ModelToken", record.Token);
                                HttpContext.Session.SetString("ModelMetric_Micro", record.ModelMetric_Micro ?? string.Empty);
                                HttpContext.Session.SetString("ModelMetric_Macro", record.ModelMetric_Macro ?? string.Empty);
                                HttpContext.Session.SetString("ModelMetric_LogLoss", record.ModelMetric_LogLoss ?? string.Empty);
                                try { await HttpContext.Session.CommitAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "Session commit failed while rehydrating from token {Token}", token); }
                                modelPath = record.ModelFileName;
                                _logger.LogInformation("Rehydrated session from token {Token} -> ModelPath={ModelPath}", token, modelPath);
                            }
                            else
                            {
                                _logger.LogWarning("Model file for token {Token} not found at expected path: {Path}", token, expectedModelPath);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error loading model record for token {Token}", token);
                    }
                }
            }

            // Additional fallback: find any model zip in the uploads folder
            if (string.IsNullOrEmpty(HttpContext.Session.GetString("ModelPath")))
            {
                try
                {
                    var candidate = Directory.GetFiles(_uploadPath, "*_MLModel.zip").FirstOrDefault()
                                ?? Directory.GetFiles(_uploadPath, "MLModel.zip").FirstOrDefault();
                    if (!string.IsNullOrEmpty(candidate))
                    {
                        var candidateName = Path.GetFileName(candidate);
                        HttpContext.Session.SetString("ModelPath", candidateName);
                        try { await HttpContext.Session.CommitAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "Session commit failed while setting fallback ModelPath"); }
                        _logger.LogInformation("Fallback: set ModelPath to {ModelPath} from uploads folder", candidateName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed searching uploads for fallback ML model.");
                }
            }

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

            try
            {
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await predictionExcelFile.CopyToAsync(stream);
                }

                _logger.LogInformation("Prediction file saved to {FilePath} as {StoredName} (exists={Exists})", filePath, storedName, System.IO.File.Exists(filePath));

                HttpContext.Session.SetString("PredictionFile", storedName);
                TempData["PredictionExcelUploaded"] = true;
                try { await HttpContext.Session.CommitAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to commit session after setting PredictionFile"); }

                // Update model record if token present
                try
                {
                    var token = HttpContext.Session.GetString("ModelToken") ?? (TempData.Peek("ModelToken") as string);
                    if (!string.IsNullOrEmpty(token))
                    {
                        var existing = await _modelStore.GetAsync(token);
                        if (existing != null)
                        {
                            existing.PredictionFileName = storedName;
                            await _modelStore.SaveAsync(existing);
                            _logger.LogInformation("Updated model record {Token} with prediction file {Prediction}", token, storedName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update model record with prediction file");
                }

                // If model still not available, mark as pending (UI will show message)
                var modelFileNow = HttpContext.Session.GetString("ModelPath");
                if (string.IsNullOrEmpty(modelFileNow) || !System.IO.File.Exists(Path.Combine(_uploadPath, modelFileNow)))
                {
                    TempData["PredictionPending"] = "Model is not ready yet. The uploaded prediction will be processed after training completes or after you load the model token.";
                    _logger.LogInformation("Prediction uploaded but model not ready for session. PredictionFile={PredictionFile} ModelPath={ModelPath}", storedName, modelFileNow);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed while saving prediction file or updating record. Exception: {Message}", ex.Message);
                TempData["PredictionError"] = "Failed to upload prediction file: " + ex.Message;
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

            // If session missed ModelPath, try to detect model file on disk (fallback)
            if (string.IsNullOrEmpty(modelFile))
            {
                try
                {
                    var candidate = Directory.GetFiles(_uploadPath, "*_MLModel.zip").FirstOrDefault()
                                ?? Directory.GetFiles(_uploadPath, "MLModel.zip").FirstOrDefault();
                    if (!string.IsNullOrEmpty(candidate))
                    {
                        modelFile = Path.GetFileName(candidate);
                        // we don't force-write to session here, just use it for the view
                        ViewBag.ModelTrained = true;
                        ViewBag.ModelPathDetected = modelFile;
                        _logger.LogInformation("Index fallback: detected model file on disk {ModelFile} in uploads", modelFile);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Index fallback: error scanning uploads for model file");
                }
            }

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
                    ViewBag.ModelToken = HttpContext.Session.GetString("ModelToken") ?? ViewBag.ModelToken;
                }
                else
                {
                    ViewBag.ModelTrained = ViewBag.ModelTrained ?? false;
                }
            }
            else
            {
                ViewBag.ModelTrained = ViewBag.ModelTrained ?? false;
            }

            if (!string.IsNullOrEmpty(predictionFile) && (ViewBag.ModelTrained as bool? == true))
            {
                var predictionPath = Path.Combine(_uploadPath, predictionFile);
                if (System.IO.File.Exists(predictionPath))
                {
                    productList = loader.LoadData(predictionPath).ToList();
                    if (productList.Any())
                    {
                        // prefer session ModelPath, fallback to detected modelFile
                        var modelToUse = HttpContext.Session.GetString("ModelPath") ?? (ViewBag.ModelPathDetected as string);
                        var predictor = new MLModelPredictor(Path.Combine(_uploadPath, modelToUse!));
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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearModel()
        {
            var token = HttpContext.Session.GetString("ModelToken");
            if (string.IsNullOrEmpty(token))
            {
                TempData["ClearResult"] = "No model token in session to clear.";
                return RedirectToAction("Index");
            }

            try
            {
                // Try to load record to know associated filenames
                var record = await _modelStore.GetAsync(token);

                if (record != null)
                {
                    // Delete files referenced by the record if they exist inside uploads directory
                    void TryDeleteFile(string fileName)
                    {
                        if (string.IsNullOrEmpty(fileName)) return;
                        try
                        {
                            var path = Path.Combine(_uploadPath, fileName);
                            // safety: ensure the path is inside _uploadPath
                            var normalizedUpload = Path.GetFullPath(_uploadPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            var normalizedPath = Path.GetFullPath(path);
                            if (!normalizedPath.StartsWith(normalizedUpload, StringComparison.OrdinalIgnoreCase)) return;

                            if (System.IO.File.Exists(normalizedPath))
                            {
                                System.IO.File.Delete(normalizedPath);
                                _logger.LogInformation("Deleted file {File}", normalizedPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed deleting file {FileName} for token {Token}", fileName, token);
                        }
                    }

                    TryDeleteFile(record.ModelFileName);
                    TryDeleteFile(record.TrainingFileName);
                    TryDeleteFile(record.PredictionFileName);
                }

                var deleted = await _modelStore.DeleteAsync(token);
                _logger.LogInformation("ModelStore.DeleteAsync({Token}) -> {Deleted}", token, deleted);

                // Clear session keys related to this model
                HttpContext.Session.Remove("ModelPath");
                HttpContext.Session.Remove("TrainingFile");
                HttpContext.Session.Remove("PredictionFile");
                HttpContext.Session.Remove("ModelToken");
                HttpContext.Session.Remove("ModelMetric_Micro");
                HttpContext.Session.Remove("ModelMetric_Macro");
                HttpContext.Session.Remove("ModelMetric_LogLoss");

                try
                {
                    await HttpContext.Session.CommitAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to commit session after clearing model tokens");
                }

                TempData["ClearResult"] = "Model record and associated files cleared.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing model for token {Token}", token);
                TempData["ClearResult"] = "Error clearing model (see logs).";
            }

            return RedirectToAction("Index");
        }
    }
}
