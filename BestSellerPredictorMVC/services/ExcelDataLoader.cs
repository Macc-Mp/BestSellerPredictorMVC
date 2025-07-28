// Services/ExcelDataLoader.cs
using OfficeOpenXml; // Add this if not present
using System; // For Console.WriteLine
using System.Collections.Generic;
using System.IO;
using BestSellerPredictorMVC.Models;

public class ExcelDataLoader
{
    public IEnumerable<ProductSalesData> LoadData(string excelFilePath)
    {
        var data = new List<ProductSalesData>();


        if (!File.Exists(excelFilePath))
        {
            Console.WriteLine($"Excel file not found: {excelFilePath}");
            return data;
        }
        else
        {
            Console.WriteLine($"Loading data from Excel file: {excelFilePath}");
        }

            try
            {
                // Remove or comment out the following line (now set in Program.cs):
                // ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
                using (var package = new ExcelPackage(new FileInfo(excelFilePath)))
                {
                    var worksheet = package.Workbook.Worksheets[0];
                    if (worksheet == null)
                    {
                        Console.WriteLine("No worksheet found in Excel file.");
                        return data;
                    }

                    int rowCount = worksheet.Dimension.End.Row;
                    int colCount = worksheet.Dimension.End.Column;

                    // Assume first row is header
                    for (int row = 2; row <= rowCount; row++)
                    {
                        try
                        {
                            var record = new ProductSalesData
                            {
                                ProductId = worksheet.Cells[row, 1].GetValue<int>(),
                                ProductName = worksheet.Cells[row, 2].GetValue<string>(),
                                Category = worksheet.Cells[row, 3].GetValue<string>(),
                                UnitPrice = worksheet.Cells[row, 4].GetValue<float>(),
                                CurrentStock = worksheet.Cells[row, 5].GetValue<float>(),
                                SalePerformanceCategory = worksheet.Cells[row, 6].GetValue<string>()
                            };
                            data.Add(record);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error reading row {row}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading Excel file: {ex.Message}");
            }

        return data;
    }

    public IEnumerable<TrainingDataExcel> LoadTrainingData(string excelFilePath)
    {
        var data = new List<TrainingDataExcel>();

        if (!File.Exists(excelFilePath))
        {
            Console.WriteLine($"Excel file not found: {excelFilePath}");
            return data;
        }

        try
        {
            using (var package = new ExcelPackage(new FileInfo(excelFilePath)))
            {
                var worksheet = package.Workbook.Worksheets[0];
                if (worksheet == null)
                {
                    Console.WriteLine("No worksheet found in Excel file.");
                    return data;
                }

                int rowCount = worksheet.Dimension.End.Row;

                for (int row = 2; row <= rowCount; row++)
                {
                    try
                    {
                        var record = new TrainingDataExcel
                        {
                            ProductId = worksheet.Cells[row, 1].GetValue<int?>() ?? 0,
                            ProductName = worksheet.Cells[row, 2].GetValue<string>() ?? string.Empty,
                            Category = worksheet.Cells[row, 3].GetValue<string>() ?? string.Empty,
                            UnitPrice = worksheet.Cells[row, 4].GetValue<float?>() ?? 0f,
                            QuantitySold = worksheet.Cells[row, 5].GetValue<int?>() ?? 0,
                            SalePerformanceCategory = worksheet.Cells[row, 6].GetValue<string>() ?? string.Empty,
                        };
                        data.Add(record);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error reading row {row}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading Excel file: {ex.Message}");
        }

        return data;
    }
}