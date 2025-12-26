using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace BestSellerPredictorMVC.Services
{
    public class ModelRecord
    {
        public string Token { get; set; } = string.Empty;
        public string ModelFileName { get; set; } = string.Empty;
        public string TrainingFileName { get; set; } = string.Empty;
        public string PredictionFileName { get; set; } = string.Empty;
        public string ModelMetric_Micro { get; set; } = string.Empty;
        public string ModelMetric_Macro { get; set; } = string.Empty;
        public string ModelMetric_LogLoss { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }

    public class ModelStore
    {
        private readonly string _storePath;

        public ModelStore()
        {
            var basePath = Environment.GetEnvironmentVariable("HOME") ?? Directory.GetCurrentDirectory();
            _storePath = Path.Combine(basePath, "modelstore");
            Directory.CreateDirectory(_storePath);
        }

        public Task SaveAsync(ModelRecord record)
        {
            var file = Path.Combine(_storePath, $"{record.Token}.json");
            var json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
            return File.WriteAllTextAsync(file, json);
        }

        public async Task<ModelRecord?> GetAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            var file = Path.Combine(_storePath, $"{token}.json");
            if (!File.Exists(file)) return null;
            var json = await File.ReadAllTextAsync(file);
            return JsonSerializer.Deserialize<ModelRecord>(json);
        }
    }
}