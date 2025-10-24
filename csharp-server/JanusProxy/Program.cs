using System.Net;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Загружаем конфигурацию
var serverConfig = builder.Configuration.GetSection("Server");
var janusConfig = builder.Configuration.GetSection("Janus");
var corsConfig = builder.Configuration.GetSection("Cors");
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
app.MapGet("/api/config", async (HttpContext context) =>
{
    Console.WriteLine("═══════════════════════════════════════════════════════");
    Console.WriteLine("[Config API] Request received");

    // Получаем токен из заголовка Authorization
    var authHeader = context.Request.Headers["Authorization"].ToString();

    // Если токен передан, пытаемся загрузить конфигурацию из ConfigService
    if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer "))
    {
        var token = authHeader.Substring("Bearer ".Length).Trim();
        Console.WriteLine($"[Config API] Token found (first 50 chars): {token.Substring(0, Math.Min(50, token.Length))}...");

        try
        {
            // Декодируем JWT токен для получения userId
            var userId = ExtractUserIdFromToken(token);
            Console.WriteLine($"[Config API] Extracted userId from token: '{userId}'");

            if (!string.IsNullOrEmpty(userId))
            {
                // Запрашиваем конфигурацию из ConfigService
                var configServiceUrl = builder.Configuration["ConfigService:Url"] ?? "http://localhost:6023";
                var requestUrl = $"{configServiceUrl}/api/configurations/internal/user/{userId}";

                Console.WriteLine($"[Config API] Requesting ConfigService:");
                Console.WriteLine($"[Config API]   URL: {requestUrl}");
                Console.WriteLine($"[Config API]   userId: {userId}");

                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

                var response = await httpClient.GetAsync(requestUrl);
                Console.WriteLine($"[Config API] ConfigService response: {response.StatusCode}");

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[Config API] ConfigService response body: {responseBody.Substring(0, Math.Min(200, responseBody.Length))}...");

                    var sipConfigData = JsonSerializer.Deserialize<SipConfigDto>(responseBody, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (sipConfigData != null)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[Config API] ✓ Loaded SIP config from ConfigService:");
                        Console.WriteLine($"[Config API]   Account: {sipConfigData.SipAccountName}@{sipConfigData.SipDomain}");
                        Console.WriteLine($"[Config API]   UserId: {sipConfigData.UserId}");
                        Console.WriteLine($"[Config API]   ProxyUri: {sipConfigData.ProxyUri}");
                        Console.ResetColor();

                        // Извлекаем сервер из ProxyUri (sip:172.16.211.135:5060 -> 172.16.211.135)
                        var server = ExtractHostFromProxyUri(sipConfigData.ProxyUri);

                        var config = new
                        {
                            janus = new { url = janusProxyPath },
                            sip = new
                            {
                                server = server,
                                proxy = sipConfigData.ProxyUri,
                                username = sipConfigData.SipAccountName,
                                password = sipConfigData.SipPassword,
                                displayName = sipConfigData.SipAccountName,
                                domain = sipConfigData.SipDomain,
                                destinationUri = "sip:2004@sip.pbx",
                                hangupDelay = 2000
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

                        Console.WriteLine("═══════════════════════════════════════════════════════");
                        return Results.Json(config);
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("[Config API] ⚠ ConfigService returned null");
                        Console.ResetColor();
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[Config API] ✗ ConfigService returned {response.StatusCode}");
                    var errorBody = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[Config API] Error body: {errorBody}");
                    Console.ResetColor();
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[Config API] ⚠ Failed to extract userId from token");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Config API] ✗ Error loading from ConfigService: {ex.Message}");
            Console.WriteLine($"[Config API] Stack trace: {ex.StackTrace}");
            Console.ResetColor();
        }
    }
    else
    {
        Console.WriteLine("[Config API] No token found in request");
    }

    // Нет токена или не удалось загрузить из ConfigService - возвращаем ошибку
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("[Config API] ✗ Unable to load configuration: no token or ConfigService unavailable");
    Console.ResetColor();
    Console.WriteLine("═══════════════════════════════════════════════════════");

    context.Response.StatusCode = 401;
    return Results.Json(new { error = "Authentication required. Please login to get configuration." });
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

// Helper методы
static string ExtractUserIdFromToken(string token)
{
    try
    {
        // JWT состоит из трех частей, разделенных точками: header.payload.signature
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            Console.WriteLine("[JWT] Invalid token format: not 3 parts");
            return string.Empty;
        }

        // Декодируем payload (вторая часть)
        var payload = parts[1];

        // Добавляем padding если нужно
        var base64 = payload.Replace('-', '+').Replace('_', '/');
        switch (payload.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }

        var jsonBytes = Convert.FromBase64String(base64);
        var jsonString = System.Text.Encoding.UTF8.GetString(jsonBytes);

        Console.WriteLine($"[JWT] Decoded payload: {jsonString}");

        // Парсим JSON и извлекаем userId
        using var doc = JsonDocument.Parse(jsonString);

        // Выводим все доступные свойства
        Console.WriteLine("[JWT] Available properties in token:");
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            Console.WriteLine($"[JWT]   - {property.Name}: {property.Value}");
        }

        // Пытаемся найти userId в разных полях
        if (doc.RootElement.TryGetProperty("sub", out var subElement))
        {
            var userId = subElement.GetString() ?? string.Empty;
            Console.WriteLine($"[JWT] Found userId in 'sub' field: {userId}");
            return userId;
        }

        if (doc.RootElement.TryGetProperty("userId", out var userIdElement))
        {
            var userId = userIdElement.GetString() ?? string.Empty;
            Console.WriteLine($"[JWT] Found userId in 'userId' field: {userId}");
            return userId;
        }

        if (doc.RootElement.TryGetProperty("nameid", out var nameIdElement))
        {
            var userId = nameIdElement.GetString() ?? string.Empty;
            Console.WriteLine($"[JWT] Found userId in 'nameid' field: {userId}");
            return userId;
        }

        Console.WriteLine("[JWT] userId not found in any expected field (sub, userId, nameid)");
        return string.Empty;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[JWT] Failed to decode token: {ex.Message}");
        Console.WriteLine($"[JWT] Stack trace: {ex.StackTrace}");
        return string.Empty;
    }
}

static string ExtractHostFromProxyUri(string proxyUri)
{
    try
    {
        // Убираем префикс sip: или sips:
        var uri = proxyUri.Replace("sip:", "").Replace("sips:", "");

        // Извлекаем хост (до двоеточия с портом)
        var colonIndex = uri.IndexOf(':');
        return colonIndex > 0 ? uri.Substring(0, colonIndex) : uri;
    }
    catch
    {
        return proxyUri;
    }
}

// DTO для десериализации ответа от ConfigService
record SipConfigDto(
    int Id,
    string UserId,
    string SipAccountName,
    string SipPassword,
    string SipDomain,
    string ProxyUri,
    string ProxyTransport,
    int RegisterTtl,
    bool IsActive
);
