using System.Net;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Загружаем конфигурацию
var serverConfig = builder.Configuration.GetSection("Server");
var janusConfig = builder.Configuration.GetSection("Janus");
var corsConfig = builder.Configuration.GetSection("Cors");
var sipConfig = builder.Configuration.GetSection("SIP");
var webrtcConfig = builder.Configuration.GetSection("WebRTC");

// Настройка URL и портов из конфигурации
var serverUrls = serverConfig["Urls"] ?? "http://localhost:8081";
builder.WebHost.UseUrls(serverUrls);

// Добавляем CORS в DI контейнер
builder.Services.AddCors();

var app = builder.Build();

// Janus API URL из конфигурации
var janusApiUrl = janusConfig["ApiUrl"] ?? "http://localhost:8088";
var janusProxyPath = janusConfig["ProxyPath"] ?? "/janus";

// Настройка CORS из конфигурации
app.UseCors(policy =>
{
    var allowAnyOrigin = corsConfig.GetValue<bool>("AllowAnyOrigin");
    if (allowAnyOrigin)
    {
        policy.AllowAnyOrigin();
    }
    else
    {
        var allowedOrigins = corsConfig.GetSection("AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins);
        }
    }

    if (corsConfig.GetValue<bool>("AllowAnyMethod"))
        policy.AllowAnyMethod();

    if (corsConfig.GetValue<bool>("AllowAnyHeader"))
        policy.AllowAnyHeader();
});

// Логирование запросов
app.Use(async (context, next) =>
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {context.Request.Method} {context.Request.Path}");
    await next();
});

// API endpoint для получения клиентской конфигурации
app.MapGet("/api/config", () =>
{
    var config = new
    {
        janus = new
        {
            url = janusProxyPath
        },
        sip = new
        {
            server = sipConfig["Server"],
            proxy = sipConfig["Proxy"],
            username = sipConfig["Username"],
            password = sipConfig["Password"],
            displayName = sipConfig["DisplayName"],
            destinationUri = sipConfig["DestinationUri"],
            hangupDelay = sipConfig.GetValue<int>("HangupDelay", 2000)
        },
        webrtc = new
        {
            stunServers = webrtcConfig.GetSection("StunServers").Get<string[]>(),
            turnServers = webrtcConfig.GetSection("TurnServers").GetChildren()
                .Select(turnServer => new
                {
                    urls = turnServer["urls"],
                    username = turnServer["username"],
                    credential = turnServer["credential"]
                })
                .ToArray(),
            iceTransportPolicy = webrtcConfig["IceTransportPolicy"],
            opusCodec = new
            {
                minPtime = webrtcConfig.GetValue<int>("OpusCodec:MinPtime", 10),
                useInbandFec = webrtcConfig.GetValue<bool>("OpusCodec:UseInbandFec", true),
                maxAverageBitrate = webrtcConfig.GetValue<int>("OpusCodec:MaxAverageBitrate", 64000),
                stereo = webrtcConfig.GetValue<bool>("OpusCodec:Stereo", false),
                cbr = webrtcConfig.GetValue<bool>("OpusCodec:Cbr", true)
            }
        }
    };
    return Results.Json(config);
});

// Janus API Proxy - обрабатываем GET и POST для /janus и /janus/*
app.Map(janusProxyPath, janusApp =>
{
    janusApp.Run(async context =>
    {
        try
        {
            var path = context.Request.Path.ToString();
            var queryString = context.Request.QueryString.ToString();

            // Формируем URL к Janus
            var targetUrl = path == "" || path == "/"
                ? $"{janusApiUrl}/janus{queryString}"
                : $"{janusApiUrl}/janus{path}{queryString}";

            var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var userAgent = context.Request.Headers["User-Agent"].ToString();

            // Извлечь session_id из пути (например, /janus/1234567890 или /janus/1234567890/handle)
            var sessionIdFromPath = "";
            var pathParts = path.TrimStart('/').Split('/');
            if (pathParts.Length > 0 && long.TryParse(pathParts[0], out var sessionId))
            {
                sessionIdFromPath = sessionId.ToString();
            }

            Console.WriteLine($"[Janus Proxy] {context.Request.Method} {path} → {targetUrl}");
            Console.WriteLine($"[Janus Proxy] Client: {clientIp} | UA: {userAgent}");
            if (!string.IsNullOrEmpty(sessionIdFromPath))
            {
                Console.WriteLine($"[Janus Proxy] Session ID from path: {sessionIdFromPath}");
            }

            using var httpClient = new HttpClient();
            HttpResponseMessage response;

            if (context.Request.Method == "GET")
            {
                response = await httpClient.GetAsync(targetUrl);
            }
            else if (context.Request.Method == "POST")
            {
                // Читаем тело запроса
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();

                // Попытка извлечь session_id из тела запроса
                try
                {
                    var jsonDoc = JsonDocument.Parse(body);
                    if (jsonDoc.RootElement.TryGetProperty("session_id", out var sessionIdElement))
                    {
                        Console.WriteLine($"[Janus Proxy] Session ID: {sessionIdElement.GetInt64()}");
                    }
                }
                catch { }

                Console.WriteLine($"[Janus Proxy] Request body: {body.Substring(0, Math.Min(100, body.Length))}...");

                var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                response = await httpClient.PostAsync(targetUrl, content);
            }
            else
            {
                context.Response.StatusCode = 405;
                await context.Response.WriteAsync("Method not allowed");
                return;
            }

            // Читаем ответ от Janus
            var responseBody = await response.Content.ReadAsStringAsync();

            // Проверяем на ошибки от Janus
            try
            {
                var jsonDoc = JsonDocument.Parse(responseBody);
                if (jsonDoc.RootElement.TryGetProperty("janus", out var janusElement) &&
                    janusElement.GetString() == "error")
                {
                    var errorCode = jsonDoc.RootElement.TryGetProperty("error", out var errorElement)
                        ? errorElement.TryGetProperty("code", out var codeElement) ? codeElement.GetInt32() : 0
                        : 0;
                    var errorReason = jsonDoc.RootElement.TryGetProperty("error", out var errorElement2)
                        ? errorElement2.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() : ""
                        : "";

                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"!!! JANUS ERROR {errorCode} !!!");
                    Console.WriteLine($"!!! From Client: {clientIp} | UA: {userAgent}");
                    if (!string.IsNullOrEmpty(sessionIdFromPath))
                    {
                        Console.WriteLine($"!!! Session ID: {sessionIdFromPath}");
                    }
                    Console.WriteLine($"!!! Reason: {errorReason}");
                    Console.ResetColor();
                }
            }
            catch { }

            Console.WriteLine($"[Janus Proxy] Response: {responseBody.Substring(0, Math.Min(200, responseBody.Length))}...");

            // Возвращаем ответ клиенту
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int)response.StatusCode;
            await context.Response.WriteAsync(responseBody);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Janus Proxy] Error: {ex.Message}");
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    });
});

// Статические файлы из папки wwwroot
app.UseDefaultFiles();
app.UseStaticFiles();

// Запуск сервера
Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
Console.WriteLine("║  Janus Proxy Server (ASP.NET Core)                         ║");
Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
Console.WriteLine($"  Environment:  {app.Environment.EnvironmentName}");
Console.WriteLine($"  HTTP:         {serverUrls}");
Console.WriteLine($"  Janus Proxy:  {serverUrls}{janusProxyPath} → {janusApiUrl}/janus");
Console.WriteLine($"  Config API:   {serverUrls}/api/config");
var staticFilesPath = builder.Configuration.GetSection("StaticFiles")["RootPath"] ?? "wwwroot";
Console.WriteLine($"  Static Files: ./{staticFilesPath}");
Console.WriteLine();
Console.WriteLine($"Откройте {serverUrls} в браузере");
Console.WriteLine();

app.Run();
