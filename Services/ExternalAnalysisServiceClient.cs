using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.Services
{
	public class ExternalAnalysisServiceClient
	{
		private readonly HttpClient _httpClient;
		private readonly string _serviceUrl;

		public ExternalAnalysisServiceClient(string serviceUrl)
		{
			_httpClient = new HttpClient();
			_httpClient.Timeout = TimeSpan.FromSeconds(5);

			_serviceUrl = serviceUrl != null && serviceUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
				? serviceUrl
				: "http://" + serviceUrl;
		}

		public async Task<Tuple<double, string>> AnalyzeAsync(object features, CancellationToken cancellationToken)
		{
			try
			{
				var json = JsonConvert.SerializeObject(features);
				var content = new StringContent(json, Encoding.UTF8, "application/json");

				var response = await _httpClient.PostAsync(_serviceUrl, content, cancellationToken);
				
					if (response.IsSuccessStatusCode)
				{
					var responseContent = await response.Content.ReadAsStringAsync();
					var result = JsonConvert.DeserializeObject<AnalysisResult>(responseContent);
					
					double score = (result != null && result.Score != 0) ? result.Score : 0.0;
					string message = (result != null && result.Message != null) ? result.Message : "";
					
					return Tuple.Create(score, message);
				}
				else
				{
					return Tuple.Create(0.0, "Service returned status code: " + response.StatusCode.ToString());
				}
			}
			catch (Exception ex)
			{
				return Tuple.Create(0.0, "Error: " + ex.Message);
			}
		}

		private class AnalysisResult
		{
			public double Score { get; set; }
			public string Message { get; set; }
		}
	}
}
