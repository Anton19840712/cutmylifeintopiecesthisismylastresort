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

**Backend:**
- **Janus Gateway** - WebRTC медиа сервер с SIP плагином (Docker)
- **C# ASP.NET Core 9.0** - прокси сервер с конфигурационным API
  - CORS настройки для dev/prod
  - `/janus` прокси эндпоинт для Janus Gateway API
  - `/api/config` эндпоинт для клиентской конфигурации
  - Конфигурационные файлы по окружениям (appsettings.json)

**Frontend:**
- **Vanilla JavaScript** - нативный WebRTC API браузера
- **Janus JavaScript API** - long polling для событий, JSON API для Janus

**Протоколы:**
- WebRTC (ICE, SDP, RTP/RTCP)
- SIP (регистрация, INVITE, BYE через Janus SIP плагин)
- STUN для NAT traversal
- Opus аудио кодек

## Архитектура и flow

```mermaid
sequenceDiagram
    participant Browser as Браузер (janus-simple.html)
    participant CSharp as C# Server :8081
    participant Janus as Janus Gateway :8088
    participant SIP as SIP Server (linphone.org)
    participant Remote as Удаленный абонент

    Note over Browser: Загрузка страницы
    Browser->>CSharp: GET /api/config
    CSharp-->>Browser: JSON с SIP/WebRTC настройками

    Note over Browser: Пользователь нажал "Позвонить"
    Browser->>CSharp: POST /janus (create session)
    CSharp->>Janus: POST /janus
    Janus-->>CSharp: {session_id}
    CSharp-->>Browser: {session_id}

    Browser->>CSharp: POST /janus/{session_id} (attach SIP plugin)
    CSharp->>Janus: POST /janus/{session_id}
    Janus-->>CSharp: {handle_id}
    CSharp-->>Browser: {handle_id}

    Browser->>CSharp: POST /janus/{session_id}/{handle_id} (register SIP)
    CSharp->>Janus: POST /janus/{session_id}/{handle_id}
    Janus->>SIP: SIP REGISTER
    SIP-->>Janus: 200 OK
    Janus-->>CSharp: event: registered
    CSharp-->>Browser: event: registered

    Browser->>Browser: getUserMedia() - захват микрофона
    Browser->>Browser: createOffer() - создание SDP
    Browser->>CSharp: POST /janus/.../message (call + jsep)
    CSharp->>Janus: POST /janus/.../message
    Janus->>SIP: SIP INVITE
    SIP->>Remote: SIP INVITE
    Remote-->>SIP: 180 Ringing
    SIP-->>Janus: 180 Ringing
    Janus-->>CSharp: event: calling
    CSharp-->>Browser: event: calling

    Remote-->>SIP: 200 OK + SDP
    SIP-->>Janus: 200 OK + SDP
    Janus-->>CSharp: event: accepted + jsep (SDP answer)
    CSharp-->>Browser: event: accepted + jsep
    Browser->>Browser: setRemoteDescription(answer)

    Note over Browser,Remote: ICE candidates exchange
    Browser->>Janus: Trickle ICE candidates
    Janus->>Browser: Remote ICE candidates

    Note over Browser,Remote: RTP Audio Stream (UDP 20000-20100)
    Browser<<->>Janus: WebRTC Audio (Opus)
    Janus<<->>Remote: SIP/RTP Audio

    Note over Browser: Пользователь нажал "Завершить"
    Browser->>CSharp: POST /janus/.../message (hangup)
    CSharp->>Janus: POST /janus/.../message
    Janus->>SIP: SIP BYE
    SIP->>Remote: SIP BYE
    Remote-->>SIP: 200 OK
    SIP-->>Janus: 200 OK
    Janus-->>CSharp: event: hangup
    CSharp-->>Browser: event: hangup
    Browser->>Browser: Закрытие PeerConnection
```
