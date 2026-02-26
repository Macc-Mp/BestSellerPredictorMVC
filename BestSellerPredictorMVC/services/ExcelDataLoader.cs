// Services/ExcelDataLoader.cs
using OfficeOpenXml; // Add this if not present
using System; // For Console.WriteLine
using System.Collections.Generic;
using System.IO;
using BestSellerPredictorMVC.Models;
using Microsoft.Extensions.Logging;
using System.Globalization;

public class ExcelDataLoader
{
    private readonly ILogger? _logger;

    public ExcelDataLoader(ILogger? logger = null)
    {
        _logger = logger;
    }

    public IEnumerable<ProductSalesData> LoadData(string excelFilePath)
    {
        var data = new List<ProductSalesData>();

        if (!File.Exists(excelFilePath))
        {
            _logger?.LogWarning("Excel file not found: {Path}", excelFilePath);
            return data;
        }

        try
        {
            // EPPlus requires license context for non-commercial use in newer versions
            ExcelPackage.License.SetNonCommercialPersonal("Moises");

            using (var package = new ExcelPackage(new FileInfo(excelFilePath)))
            {
                var worksheet = package.Workbook.Worksheets.Count > 0 ? package.Workbook.Worksheets[0] : null;
                if (worksheet == null)
                {
                    _logger?.LogWarning("No worksheet found in Excel file: {Path}", excelFilePath);
                    return data;
                }

                if (worksheet.Dimension == null)
                {
                    _logger?.LogWarning("Worksheet has no data in file: {Path}", excelFilePath);
                    return data;
                }

                int rowCount = worksheet.Dimension.End.Row;

                // Assume first row is header
                for (int row = 2; row <= rowCount; row++)
                {
                    try
                    {
                        var pidText = worksheet.Cells[row, 1].Text?.Trim() ?? string.Empty;
                        var nameText = worksheet.Cells[row, 2].Text?.Trim() ?? string.Empty;
                        var categoryText = worksheet.Cells[row, 3].Text?.Trim() ?? string.Empty;
                        var unitPriceText = worksheet.Cells[row, 4].Text?.Trim() ?? string.Empty;
                        var currentStockText = worksheet.Cells[row, 5].Text?.Trim() ?? string.Empty;
                        var labelText = worksheet.Cells[row, 6].Text?.Trim() ?? string.Empty;

                        // Keep ProductId as-is (string) to preserve alphanumeric IDs like "P1001"
                        var productId = pidText;

                        float unitPrice = 0f;
                        if (!float.TryParse(unitPriceText, NumberStyles.Any, CultureInfo.InvariantCulture, out unitPrice))
                            unitPrice = 0f;

                        float currentStock = 0f;
                        if (!float.TryParse(currentStockText, NumberStyles.Any, CultureInfo.InvariantCulture, out currentStock))
                            currentStock = 0f;

                        var record = new ProductSalesData
                        {
                            ProductId = productId,
                            ProductName = nameText,
                            Category = categoryText,
                            UnitPrice = unitPrice,
                            CurrentStock = currentStock,
                            SalePerformanceCategory = labelText
                        };
                        data.Add(record);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Error reading row {Row} from {Path}: {Message}", row, excelFilePath, ex.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error loading Excel file: {Path} -> {Message}", excelFilePath, ex.Message);
        }

        return data;
    }

    public IEnumerable<TrainingDataExcel> LoadTrainingData(string excelFilePath)
    {
        var data = new List<TrainingDataExcel>();
        if (!File.Exists(excelFilePath))
        {
            _logger?.LogWarning("Training Excel file not found: {Path}", excelFilePath);
            return data;
        }

        try
        {
            ExcelPackage.License.SetNonCommercialPersonal("Moises");
            //ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
            using (var package = new ExcelPackage(new FileInfo(excelFilePath)))
            {
                var worksheet = package.Workbook.Worksheets.Count > 0 ? package.Workbook.Worksheets[0] : null;
                if (worksheet == null)
                {
                    _logger?.LogWarning("No worksheet found in Excel file: {Path}", excelFilePath);
                    return data;
                }

                if (worksheet.Dimension == null)
                {
                    _logger?.LogWarning("Worksheet has no data in file: {Path}", excelFilePath);
                    return data;    
                }

                int rowCount = worksheet.Dimension.End.Row;
            
                for (int row = 2; row <= rowCount; row++)
                {
                    try
                    {
                        var pidText = worksheet.Cells[row, 1].Text?.Trim() ?? string.Empty;
                        var nameText = worksheet.Cells[row, 2].Text?.Trim() ?? string.Empty;
                        var categoryText = worksheet.Cells[row, 3].Text?.Trim() ?? string.Empty;
                        var unitPriceText = worksheet.Cells[row, 4].Text?.Trim() ?? string.Empty;
                        var quantityText = worksheet.Cells[row, 5].Text?.Trim() ?? string.Empty;
                        var labelText = worksheet.Cells[row, 6].Text?.Trim() ?? string.Empty;

                        // Preserve ProductId as string (e.g., "P1001")
                        var productId = pidText;

                        float unitPrice = 0f;
                        if (!float.TryParse(unitPriceText, NumberStyles.Any, CultureInfo.InvariantCulture, out unitPrice))
                            unitPrice = 0f;

                        int quantity = 0;
                        if (!int.TryParse(quantityText, NumberStyles.Any, CultureInfo.InvariantCulture, out quantity))
                            quantity = 0;

                        var record = new TrainingDataExcel
                        {
                            ProductId = productId,
                            ProductName = nameText,
                            Category = categoryText,
                            UnitPrice = unitPrice,
                            QuantitySold = quantity,
                            SalePerformanceCategory = labelText,
                        };
                        data.Add(record);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Error reading training row {Row} from {Path}: {Message}", row, excelFilePath, ex.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error loading training Excel file: {Path} -> {Message}", excelFilePath, ex.Message);
        }

        return data;

    }

}

