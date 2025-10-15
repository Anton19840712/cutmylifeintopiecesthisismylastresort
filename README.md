# SIP WebRTC Project with Janus Gateway

## Структура проекта

```
C:\попытка sip\
├── docker-compose.yml          # Docker конфигурация для Janus Gateway
├── janus-config/               # Конфигурация Janus Gateway
└── csharp-server/              # C# ASP.NET Core прокси сервер
    └── JanusProxy/
        ├── Program.cs          # Основной файл сервера
        └── wwwroot/            # Статические файлы (HTML клиенты)
            ├── index.html      # Главная страница с JsSIP
            ├── janus-simple.html  # Простой Janus SIP клиент
            └── adapter.js      # WebRTC адаптер
```

## Запуск

1. Запустите Janus Gateway в Docker:
   ```bash
   docker-compose up -d
   ```

2. Запустите C# прокси сервер:
   ```bash
   cd csharp-server/JanusProxy
   dotnet run
   ```

3. Откройте в браузере:
   - http://localhost:8081 - главная страница
   - http://localhost:8081/janus-simple.html - простой Janus клиент

## Порты

- 8081 - C# прокси сервер (HTTP + статические файлы)
- 8088 - Janus Gateway (HTTP API)
- 20000-20100/udp - RTP медиа потоки

## Технологии

- **Janus Gateway** - WebRTC сервер в Docker
- **ASP.NET Core 9.0** - C# прокси сервер
- **JsSIP** - JavaScript SIP библиотека
