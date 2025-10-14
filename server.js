const WebSocket = require('ws');
const dgram = require('dgram');
const express = require('express');
const path = require('path');
const fetch = require('node-fetch');

const SIP_SERVER = 'sip.linphone.org';
const SIP_PORT = 5060;
const WS_PORT = 8089;  // Изменено с 8088 на 8089 (8088 для Janus)
const HTTP_PORT = 8080;
const JANUS_API = 'http://localhost:8088';

// Создаем HTTP сервер для статических файлов
const app = express();
app.use(express.json());

// Прокси для Janus API (POST и GET запросов для long-polling)
// Только для /janus и /janus/... (не для /janus-simple.html и других файлов)
const handleJanusProxy = async (req, res) => {
    try {
        // Формируем полный URL: /janus + путь после /janus + query params для GET
        let targetUrl = req.path === '/janus' ? `${JANUS_API}/janus` : `${JANUS_API}${req.path}`;

        // Добавляем query параметры для GET запросов (long-polling)
        if (req.method === 'GET' && Object.keys(req.query).length > 0) {
            const queryString = new URLSearchParams(req.query).toString();
            targetUrl += `?${queryString}`;
        }

        console.log(`[Janus Proxy] ${req.method} ${req.path} → ${targetUrl}`);

        const fetchOptions = {
            method: req.method,
            headers: {
                'Content-Type': 'application/json'
            }
        };

        // Добавляем body только для POST запросов
        if (req.method === 'POST') {
            fetchOptions.body = JSON.stringify(req.body);
        }

        const response = await fetch(targetUrl, fetchOptions);
        const data = await response.json();
        console.log(`[Janus Proxy] Response:`, JSON.stringify(data).substring(0, 200));
        res.json(data);
    } catch (error) {
        console.error('[Janus Proxy] Error:', error.message);
        res.status(500).json({ error: error.message });
    }
};

// Exact match for /janus
app.all('/janus', handleJanusProxy);
// Match /janus/ followed by anything (session paths)
app.all('/janus/*', handleJanusProxy);

// Статические файлы (после прокси, чтобы не перехватывать API запросы)
app.use(express.static(path.join(__dirname, 'public')));

app.listen(HTTP_PORT, () => {
    console.log(`✓ HTTP сервер запущен: http://localhost:${HTTP_PORT}`);
});

// Создаем WebSocket сервер для SIP (JsSIP клиентов)
const wss = new WebSocket.Server({ port: WS_PORT });
console.log(`✓ WebSocket SIP сервер запущен на ws://localhost:${WS_PORT} (для JsSIP)`);

// Маппинг WebSocket клиентов к UDP сокетам
const clientSockets = new Map();

wss.on('connection', (ws, req) => {
    const clientId = `${req.socket.remoteAddress}:${req.socket.remotePort}`;
    console.log(`\n[${new Date().toISOString()}] Новое WebSocket соединение: ${clientId}`);

    // Создаем UDP сокет для этого клиента
    const udpSocket = dgram.createSocket('udp4');

    // Привязываем к случайному порту для получения ответов
    udpSocket.bind(() => {
        const addr = udpSocket.address();
        console.log(`[${clientId}] UDP сокет слушает на ${addr.address}:${addr.port}`);
    });

    clientSockets.set(ws, udpSocket);

    // Получение ответов от SIP сервера
    udpSocket.on('message', (msg, rinfo) => {
        console.log(`[${clientId}] ← SIP сервер: ${msg.length} байт от ${rinfo.address}:${rinfo.port}`);

        // Декодируем для логирования
        const decoded = msg.toString('utf8');
        const firstLine = decoded.split('\r\n')[0] || decoded.split('\n')[0];
        console.log(`   ${firstLine}`);

        try {
            // Отправляем SIP ответ обратно в браузер через WebSocket как текст
            if (ws.readyState === WebSocket.OPEN) {
                ws.send(decoded);
            }
        } catch (err) {
            console.error(`[${clientId}] Ошибка отправки в WebSocket:`, err.message);
        }
    });

    udpSocket.on('error', (err) => {
        console.error(`[${clientId}] Ошибка UDP:`, err.message);
    });

    // Получение SIP сообщений от браузера через WebSocket
    ws.on('message', (message) => {
        const sipMessage = message.toString();
        const lines = sipMessage.split('\n');
        const firstLine = lines[0];
        const method = firstLine.split(' ')[0];

        console.log(`\n[${clientId}] → SIP сервер: ${sipMessage.length} байт [${method}]`);
        console.log(firstLine);

        // ПОЛНЫЙ ДЛЯ ОТЛАДКИ
        console.log('--- ПОЛНОЕ SIP СООБЩЕНИЕ ---');
        console.log(sipMessage);
        console.log('--- КОНЕЦ ---');

        try {
            // Получаем локальный адрес UDP сокета
            let localAddr = '127.0.0.1';
            let localPort = 5060;
            try {
                const addr = udpSocket.address();
                localAddr = addr.address || '127.0.0.1';
                localPort = addr.port || 5060;
            } catch (e) {
                // Если сокет еще не привязан, используем значения по умолчанию
            }

            // Переписываем Via header: заменяем WS на UDP и invalid адрес на реальный
            let modifiedMessage = sipMessage.replace(
                /Via:\s*SIP\/2\.0\/WS\s+[^\s;]+/gi,
                `Via: SIP/2.0/UDP ${localAddr}:${localPort}`
            );

            // Также заменяем Contact header
            modifiedMessage = modifiedMessage.replace(
                /Contact:\s*<sip:([^@]+)@[^>;]+;transport=ws>/gi,
                `Contact: <sip:$1@${localAddr}:${localPort};transport=udp>`
            );

            console.log('--- МОДИФИЦИРОВАННОЕ SIP ---');
            console.log(modifiedMessage.substring(0, 300));
            console.log('---');

            // Пересылаем модифицированное SIP сообщение на sip.linphone.org через UDP
            const buffer = Buffer.from(modifiedMessage);
            udpSocket.send(buffer, 0, buffer.length, SIP_PORT, SIP_SERVER, (err) => {
                if (err) {
                    console.error(`[${clientId}] Ошибка отправки UDP:`, err.message);
                } else {
                    console.log(`[${clientId}] ✓ Отправлено на ${SIP_SERVER}:${SIP_PORT}`);
                }
            });
        } catch (err) {
            console.error(`[${clientId}] Ошибка обработки сообщения:`, err.message);
        }
    });

    ws.on('close', () => {
        console.log(`[${clientId}] WebSocket соединение закрыто`);
        udpSocket.close();
        clientSockets.delete(ws);
    });

    ws.on('error', (err) => {
        console.error(`[${clientId}] WebSocket ошибка:`, err.message);
    });
});

console.log(`\n╔════════════════════════════════════════════════════════════╗`);
console.log(`║  WebSocket-SIP Proxy Server + Janus Proxy                 ║`);
console.log(`╚════════════════════════════════════════════════════════════╝`);
console.log(`  HTTP:         http://localhost:${HTTP_PORT}`);
console.log(`  WebSocket:    ws://localhost:${WS_PORT} (JsSIP)`);
console.log(`  Janus Proxy:  http://localhost:${HTTP_PORT}/janus → http://localhost:8088/janus`);
console.log(`  SIP Target:   ${SIP_SERVER}:${SIP_PORT} (UDP)`);
console.log(`\nОткройте http://localhost:${HTTP_PORT} в браузере для тестирования\n`);
