using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OsmoOverlay.Core.Updates;
using SkiaSharp;

namespace OsmoOverlay.Gui;

public partial class WelcomeWindow
{
	private const string ConnectionHost = "server.arcgisonline.com";
	private static readonly HttpClient ConnectionHttp = CreateConnectionHttp();
	private readonly CancellationTokenSource _connectionLifetime = new();
	private bool _checkingConnection;

	private static HttpClient CreateConnectionHttp()
	{
		var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		client.DefaultRequestHeaders.UserAgent.ParseAdd($"OsmoOverlay/{AppUpdates.CurrentVersion} (+{AppUpdates.RepositoryUrl})");
		return client;
	}

	private async void OnConnectionRetryClick(object? sender, RoutedEventArgs e)
	{
		await CheckConnectionAsync();
	}

	private async Task CheckConnectionAsync()
	{
		if (_checkingConnection || _connectionLifetime.IsCancellationRequested) return;
		_checkingConnection = true;
		ConnectionRetryButton.IsEnabled = false;
		ConnectionHint.Text = Strings.Welcome_ConnectionChecking;
		SetConnectionStatus(ConnectionDnsStatus, Strings.Welcome_Checking, null);
		SetConnectionStatus(ConnectionHttpsStatus, Strings.Welcome_Checking, null);
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_connectionLifetime.Token);
		timeout.CancelAfter(TimeSpan.FromSeconds(12));

		try
		{
			Task<bool> dns = CheckDnsAsync(timeout.Token);
			Task<bool> https = CheckMapHttpsAsync(timeout.Token);
			await Task.WhenAll(dns, https);
			if (_connectionLifetime.IsCancellationRequested) return;

			ConnectionHint.Text = (dns.Result, https.Result) switch
			{
				(true, true) => Strings.Welcome_ConnectionOk,
				(false, true) => Strings.Welcome_ConnectionProxy,
				(false, false) => Strings.Welcome_ConnectionDnsHelp,
				_ => Strings.Welcome_ConnectionHttpsHelp
			};
		}
		finally
		{
			_checkingConnection = false;
			if (!_connectionLifetime.IsCancellationRequested) ConnectionRetryButton.IsEnabled = true;
		}
	}

	private async Task<bool> CheckDnsAsync(CancellationToken ct)
	{
		try
		{
			IPAddress[] addresses = await Dns.GetHostAddressesAsync(ConnectionHost, ct);
			bool ok = addresses.Length > 0;
			if (!_connectionLifetime.IsCancellationRequested)
				SetConnectionStatus(ConnectionDnsStatus, ok ? Strings.Welcome_CheckOk : Strings.Welcome_ConnectionDnsFailed, ok);
			return ok;
		}
		catch (Exception ex) when (ex is SocketException or OperationCanceledException)
		{
			if (!_connectionLifetime.IsCancellationRequested)
				SetConnectionStatus(ConnectionDnsStatus, ex is OperationCanceledException ? Strings.Welcome_ConnectionTimeout : Strings.Welcome_ConnectionDnsFailed, false);
			return false;
		}
	}

	private async Task<bool> CheckMapHttpsAsync(CancellationToken ct)
	{
		try
		{
			using HttpResponseMessage response = await ConnectionHttp.GetAsync(
				$"https://{ConnectionHost}/ArcGIS/rest/services/World_Imagery/MapServer/tile/0/0/0", HttpCompletionOption.ResponseHeadersRead, ct);
			if (!response.IsSuccessStatusCode)
			{
				if (!_connectionLifetime.IsCancellationRequested)
					SetConnectionStatus(ConnectionHttpsStatus, $"HTTP {(int)response.StatusCode}", false);
				return false;
			}

			using Stream stream = await response.Content.ReadAsStreamAsync(ct);
			using var data = new MemoryStream();
			byte[] buffer = new byte[8192];
			int read;
			while ((read = await stream.ReadAsync(buffer, ct)) > 0)
			{
				if (data.Length + read > 256 * 1024) return InvalidMapResponse();
				data.Write(buffer, 0, read);
			}

			using SKData imageData = SKData.CreateCopy(data.ToArray());
			using SKCodec? codec = SKCodec.Create(imageData);
			if (codec is null || codec.Info.Width != 256 || codec.Info.Height != 256) return InvalidMapResponse();
			using var bitmap = new SKBitmap(codec.Info);
			if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) return InvalidMapResponse();
			if (!_connectionLifetime.IsCancellationRequested) SetConnectionStatus(ConnectionHttpsStatus, Strings.Welcome_CheckOk, true);
			return true;
		}
		catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
		{
			string message = ex switch
			{
				OperationCanceledException => Strings.Welcome_ConnectionTimeout,
				HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => Strings.Welcome_ConnectionTlsFailed,
				_ => Strings.Welcome_ConnectionFailed
			};
			if (!_connectionLifetime.IsCancellationRequested) SetConnectionStatus(ConnectionHttpsStatus, message, false);
			return false;
		}
	}

	private bool InvalidMapResponse()
	{
		if (!_connectionLifetime.IsCancellationRequested)
			SetConnectionStatus(ConnectionHttpsStatus, Strings.Welcome_ConnectionInvalidResponse, false);
		return false;
	}

	private static void SetConnectionStatus(TextBlock text, string message, bool? success)
	{
		text.Text = message;
		text.Foreground = success switch { true => Palette.Success, false => Palette.Warning, _ => Palette.TextMuted };
	}
}
