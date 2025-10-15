using System.Net;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Настройка URL и портов
builder.WebHost.UseUrls("http://localhost:8081");

// Добавляем CORS в DI контейнер
builder.Services.AddCors();

var app = builder.Build();

// Константы
const string JANUS_API = "http://localhost:8088";

// Настройка CORS
app.UseCors(policy => policy
    .AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader());

// Логирование запросов
app.Use(async (context, next) =>
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {context.Request.Method} {context.Request.Path}");
    await next();
});

// Janus API Proxy - обрабатываем GET и POST для /janus и /janus/*
app.Map("/janus", janusApp =>
{
    janusApp.Run(async context =>
    {
        try
        {
            var path = context.Request.Path.ToString();
            var queryString = context.Request.QueryString.ToString();

            // Формируем URL к Janus
            var targetUrl = path == "" || path == "/"
                ? $"{JANUS_API}/janus{queryString}"
                : $"{JANUS_API}/janus{path}{queryString}";

            Console.WriteLine($"[Janus Proxy] {context.Request.Method} {path} → {targetUrl}");

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
Console.WriteLine("║  Janus Proxy Server (ASP.NET Core)                        ║");
Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
Console.WriteLine($"  HTTP:         http://localhost:8081");
Console.WriteLine($"  Janus Proxy:  http://localhost:8081/janus → {JANUS_API}/janus");
Console.WriteLine($"  Static Files: ./wwwroot");
Console.WriteLine();
Console.WriteLine("Откройте http://localhost:8081 в браузере");
Console.WriteLine();

app.Run();
