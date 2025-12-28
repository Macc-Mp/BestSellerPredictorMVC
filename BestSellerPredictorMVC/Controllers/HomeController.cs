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
            // Improved logging for debugging
            _logger.LogInformation("UploadPredictionExcel called. SessionId={SessionId} Cookies={Cookies} SessionAvailable={IsAvailable} ModelPath={ModelPath} TrainingFile={TrainingFile} PredictionFileBefore={PredictionFile}",
                HttpContext.Session?.Id,
                Request.Headers["Cookie"].ToString(),
                HttpContext.Session.IsAvailable,
                HttpContext.Session.GetString("ModelPath"),
                HttpContext.Session.GetString("TrainingFile"),
                HttpContext.Session.GetString("PredictionFile"));

            // If model path is missing in session, attempt to recover it from a token:
            var modelPath = HttpContext.Session.GetString("ModelPath");
            if (string.IsNullOrEmpty(modelPath))
            {
                string token = null;

                // 1) Check TempData (use Peek so we don't consume it)
                try { token ??= TempData.Peek("ModelToken") as string; } catch { /* ignore */ }

                // 2) Check form (hidden input posted with the prediction form)
                if (string.IsNullOrEmpty(token) && Request.HasFormContentType && Request.Form.ContainsKey("token"))
                {
                    token = Request.Form["token"].ToString();
                }

                // 3) Check query string
                if (string.IsNullOrEmpty(token) && Request.Query.ContainsKey("token"))
                {
                    token = Request.Query["token"].ToString();
                }

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
                                // Rehydrate session so subsequent logic finds the model
                                HttpContext.Session.SetString("ModelPath", record.ModelFileName);
                                if (!string.IsNullOrEmpty(record.TrainingFileName))
                                    HttpContext.Session.SetString("TrainingFile", record.TrainingFileName);
                                HttpContext.Session.SetString("ModelToken", record.Token);
                                HttpContext.Session.SetString("ModelMetric_Micro", record.ModelMetric_Micro ?? string.Empty);
                                HttpContext.Session.SetString("ModelMetric_Macro", record.ModelMetric_Macro ?? string.Empty);
                                HttpContext.Session.SetString("ModelMetric_LogLoss", record.ModelMetric_LogLoss ?? string.Empty);

                                try { await HttpContext.Session.CommitAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "Session commit failed while rehydrating from token {Token}", token); }

                                modelPath = record.ModelFileName;
                                _logger.LogInformation("Rehydrated session from token {Token} -> ModelPath={ModelPath}", token, modelPath);
                                TempData["LoadTokenSuccess"] = "Session rehydrated from token.";
                            }
                            else
                            {
                                _logger.LogWarning("Model file for token {Token} not found at expected path: {Path}", token, expectedModelPath);
                                TempData["LoadTokenError"] = "Saved model file not found on server.";
                            }
                        }
                        else
                        {
                            _logger.LogWarning("No record found in ModelStore for token {Token}", token);
                            TempData["LoadTokenError"] = "Model token not found.";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error loading model record for token {Token}", token);
                        TempData["LoadTokenError"] = "Error loading model token (see logs).";
                    }
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
                // Save incoming prediction file
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await predictionExcelFile.CopyToAsync(stream);
                }

                _logger.LogInformation("Prediction file saved to {FilePath} as {StoredName} (exists={Exists})", filePath, storedName, System.IO.File.Exists(filePath));

                // Save to session so Index action can pick it up
                HttpContext.Session.SetString("PredictionFile", storedName);
                TempData["PredictionExcelUploaded"] = true;

                try
                {
                    await HttpContext.Session.CommitAsync();
                    _logger.LogInformation("Session committed after setting PredictionFile={PredictionFile}", storedName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to commit session after setting PredictionFile");
                }

                // If we have a token for this model, update the persistent record with the prediction filename
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
                        else
                        {
                            _logger.LogWarning("No model record found for token {Token} when saving prediction file {Prediction}", token, storedName);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("No ModelToken available to associate prediction file {Prediction}", storedName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update model record with prediction file");
                }

                // If model is not yet available in session or model file doesn't exist yet, mark as pending
                var modelFileNow = HttpContext.Session.GetString("ModelPath");
                if (string.IsNullOrEmpty(modelFileNow) || !System.IO.File.Exists(Path.Combine(_uploadPath, modelFileNow)))
                {
                    TempData["PredictionPending"] = "Model is not ready yet. The uploaded prediction will be processed after training completes.";
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
                    // expose token to the view if present
                    ViewBag.ModelToken = HttpContext.Session.GetString("ModelToken");
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
