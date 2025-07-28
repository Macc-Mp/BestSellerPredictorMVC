// Services/SqlServerDataService.cs
using Microsoft.Data.SqlClient; // Add this if not present (this is for SQL Server)
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using SqlCommand = Microsoft.Data.SqlClient.SqlCommand; // For .ToList() if needed elsewhere
using BestSellerPredictorMVC.Models;
public class SqlServerDataService
{
    private readonly string _connectionString;

    public SqlServerDataService(string connectionString)
    {
        _connectionString = connectionString;
    }

    private Microsoft.Data.SqlClient.SqlConnection GetConnection() // Updated to use Microsoft.Data.SqlClient.SqlConnection
    {
        return new Microsoft.Data.SqlClient.SqlConnection(_connectionString);
    }

    public void UpdateProductPerformance(int productId, string performanceCategory)
    {
        using (var connection = GetConnection())
        {
            connection.Open();
            string query = "UPDATE Products SET LastEvaluatedPerformanceCategory = @category, LastEvaluationDate = GETDATE() WHERE ProductId = @productId";
            using (var command = new SqlCommand(query, connection)) // No changes needed here as SqlCommand is from Microsoft.Data.SqlClient
            {
                command.Parameters.AddWithValue("@category", performanceCategory);
                command.Parameters.AddWithValue("@productId", productId);
                command.ExecuteNonQuery();
            }
        }
    }

    public void InsertBestSeller(int productId, string reason)
    {
        using (var connection = GetConnection())
        {
            connection.Open();
            string query = "INSERT INTO BestSellers (ProductId, ReasonForBestSeller, EvaluationDate) VALUES (@productId, @reason, GETDATE())";
                using (var command = new SqlCommand(query, connection)) // No changes needed here as SqlCommand is from Microsoft.Data.SqlClient
            {
                command.Parameters.AddWithValue("@productId", productId);
                command.Parameters.AddWithValue("@reason", reason);
                command.ExecuteNonQuery();
            }
        }
    }

    public void InsertUnderperformingProduct(int productId, string reason)
    {
        using (var connection = GetConnection())
        {
            connection.Open();
            string query = "INSERT INTO UnderperformingProducts (ProductId, ReasonForUnderperformance, EvaluationDate) VALUES (@productId, @reason, GETDATE())";
            using (var command = new SqlCommand(query, connection)) // No changes needed here as SqlCommand is from Microsoft.Data.SqlClient
            {
                command.Parameters.AddWithValue("@productId", productId);
                command.Parameters.AddWithValue("@reason", reason);
                command.ExecuteNonQuery();
            }
        }
    }

    public void ClearBestSellersAndUnderperforming()
    {
        using (var connection = GetConnection())
        {
            connection.Open();
            using (var command = new SqlCommand("DELETE FROM BestSellers;", connection)) // No changes needed here as SqlCommand is from Microsoft.Data.SqlClient
            {
                command.ExecuteNonQuery();
            }
            using (var command = new SqlCommand("DELETE FROM UnderperformingProducts;", connection)) // No changes needed here as SqlCommand is from Microsoft.Data.SqlClient
            {
                command.ExecuteNonQuery();
            }
        }
    }

    public IEnumerable<ProductSalesData> GetProductsForPrediction()
    {
        var products = new List<ProductSalesData>();
        using (var connection = GetConnection())
        {
            connection.Open();
            string query = "SELECT ProductId, ProductName, Category, UnitPrice, CurrentStock FROM Products";
            using (var command = new SqlCommand(query, connection)) // No changes needed here as SqlCommand is from Microsoft.Data.SqlClient
            {
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        // Handle possible decimal/double types and nulls for UnitPrice and CurrentStock
                        float unitPrice = 0f;
                        float currentStock = 0f;

                        int unitPriceOrdinal = reader.GetOrdinal("UnitPrice");
                        int currentStockOrdinal = reader.GetOrdinal("CurrentStock");

                        if (!reader.IsDBNull(unitPriceOrdinal))
                        {
                            // Convert from decimal/double to float safely
                            var value = reader.GetValue(unitPriceOrdinal);
                            if (value is decimal dec)
                                unitPrice = (float)dec;
                            else if (value is double dbl)
                                unitPrice = (float)dbl;
                            else if (value is float flt)
                                unitPrice = flt;
                            else
                                unitPrice = Convert.ToSingle(value);
                        }

                        if (!reader.IsDBNull(currentStockOrdinal))
                        {
                            var value = reader.GetValue(currentStockOrdinal);
                            if (value is decimal dec)
                                currentStock = (float)dec;
                            else if (value is double dbl)
                                currentStock = (float)dbl;
                            else if (value is float flt)
                                currentStock = flt;
                            else
                                currentStock = Convert.ToSingle(value);
                        }

                        products.Add(new ProductSalesData
                        {
                            ProductId = reader.GetInt32(reader.GetOrdinal("ProductId")),
                            ProductName = reader.GetString(reader.GetOrdinal("ProductName")),
                            Category = reader.GetString(reader.GetOrdinal("Category")),
                            UnitPrice = unitPrice,
                            CurrentStock = currentStock,
                            SalePerformanceCategory = null
                        });
                    }
                }
            }
        }
        return products;
    }
}