<?php
header('Content-Type: application/json');

$log_file = __DIR__ . '/bridge_log.txt';

function write_to_log($message) {
    global $log_file;
    $timestamp = date('[Y-m-d H:i:s] ');
    file_put_contents($log_file, $timestamp . $message . "\n", FILE_APPEND);
}

write_to_log("--- СТАРТ УЛЬТИМАТИВНОГО ОДНОХОДОВОГО ПОСТА ---");

// 1. Проверяем токен авторизации шлюза
$secret_token = "MistmareTgBridge2026Secret"; 
$headers = getallheaders();

if (!isset($headers['X-Bridge-Token']) || $headers['X-Bridge-Token'] !== $secret_token) {
    write_to_log("ОШИБКА: Неверный или отсутствующий X-Bridge-Token.");
    http_response_code(403);
    echo json_encode(["status" => "error", "message" => "Access denied"]);
    exit;
}

// 2. Читаем входящий JSON от C# приложения
$input = file_get_contents('php://input');
$data = json_decode($input, true);

if (!$data || !isset($data['bot_token']) || !isset($data['chat_id']) || !isset($data['text'])) {
    write_to_log("ОШИБКА: Неверный формат данных.");
    http_response_code(400);
    echo json_encode(["status" => "error", "message" => "Invalid data format"]);
    exit;
}

$bot_token = trim($data['bot_token']);
$chat_id = trim($data['chat_id']);
$text = $data['text'];
$image_base64 = isset($data['image_base64']) ? $data['image_base64'] : '';

// 3. Если картинка передана — сохраняем её на хостинг и делаем скрытую ссылку
if (!empty($image_base64)) {
    write_to_log("Обнаружена обложка. Сохраняю на веб-сервер...");
    
    // Генерируем уникальное имя файла
    $image_name = 'img_' . uniqid() . '.jpg';
    $public_image_path = __DIR__ . '/tg_images/' . $image_name;
    
    $image_data = base64_decode($image_base64);
    file_put_contents($public_image_path, $image_data);
    
    // Формируем публичный URL картинки на вашем сайте
    $image_url = "https://" . $_SERVER['HTTP_HOST'] . "/tg_images/" . $image_name;
    write_to_log("Картинка сохранена по адресу: " . $image_url);
    
    // Внедряем невидимую ссылку на картинку в самое начало текста
    // Символ &#8203; — это невидимый пробел нулевой ширины
    $text = '<a href="' . $image_url . '">&#8203;</a>' . $text;
}

// 4. Отправляем ОДИН запрос sendMessage с принудительным переносом картинки НАВЕРХ
$url = "https://" . "api.telegram.org/bot" . $bot_token . "/sendMessage";

$payload = json_encode([
    'chat_id' => $chat_id,
    'text' => $text,
    'parse_mode' => 'HTML',
    // Используем современную структуру настроек превью для Telegram API
    'link_preview_options' => [
        'is_disabled' => false,
        'url' => !empty($image_url) ? $image_url : null,
        'prefer_small_media' => false, // Делаем картинку большой, во всю ширину поста
        'prefer_large_media' => true,
        'show_above_text' => true      // 🔥 ВОТ ОН: Переносит обложку в самый верх над текстом!
    ]
], JSON_UNESCAPED_UNICODE);

$ch = curl_init();
curl_setopt($ch, CURLOPT_URL, $url);
curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
curl_setopt($ch, CURLOPT_POST, true);
curl_setopt($ch, CURLOPT_POSTFIELDS, $payload);
curl_setopt($ch, CURLOPT_HTTPHEADER, [
    'Content-Type: application/json',
    'Content-Length: ' . strlen($payload)
]);

$response = curl_exec($ch);
$http_code = curl_getinfo($ch, CURLINFO_HTTP_CODE);

if (curl_errno($ch)) {
    write_to_log("КРИТИЧЕСКАЯ ОШИБКА CURL: " . curl_error($ch));
}
curl_close($ch);


write_to_log("HTTP код ответа от Telegram: " . $http_code);
write_to_log("Ответ от Telegram API: " . $response);

if ($http_code === 200) {
    write_to_log("РЕЗУЛЬТАТ: Пост-монолит успешно опубликован.");
    echo json_encode(["status" => "success", "message" => "Successfully posted to Telegram"]);
} else {
    write_to_log("ОШИБКА: Telegram отклонил запрос с кодом " . $http_code);
    http_response_code($http_code !== 0 ? $http_code : 500);
    echo $response;
    exit;
}
?>
