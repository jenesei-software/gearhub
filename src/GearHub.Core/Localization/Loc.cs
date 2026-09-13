namespace GearHub.Core.Localization;

/// <summary>
/// Mini localization without resx: the six most popular Windows languages (en, ru, de, fr, es, zh),
/// with English as fallback. The app picks the language from Windows settings and calls
/// <see cref="Use"/>; strings are retrieved via <see cref="Get"/> and <see cref="Format"/>.
/// </summary>
public static class Loc
{
    private static readonly string[] LanguageCodes = ["en", "ru", "de", "fr", "es", "zh"];

    /// <summary>Translations: the order of values matches <see cref="LanguageCodes"/>.</summary>
    private static readonly (string Key, string[] Values)[] Rows =
    [
        ("StatusSearching", ["Searching for devices…", "Поиск устройств…", "Geräte werden gesucht…", "Recherche des appareils…", "Buscando dispositivos…", "正在搜索设备…"]),
        ("StatusNoDevices", ["No devices found", "Устройства не найдены", "Keine Geräte gefunden", "Aucun appareil trouvé", "No se encontraron dispositivos", "未找到设备"]),
        ("StatusCount", ["{0} devices · online: {1}", "{0} устр. · онлайн: {1}", "{0} Geräte · online: {1}", "{0} appareils · en ligne : {1}", "{0} dispositivos · en línea: {1}", "{0} 台设备 · 在线：{1}"]),
        ("StatusErrors", [" · errors: ", " · ошибки: ", " · Fehler: ", " · erreurs : ", " · errores: ", " · 错误："]),
        ("StatusScanError", ["Scan error: {0}", "Ошибка сканирования: {0}", "Scanfehler: {0}", "Erreur d’analyse : {0}", "Error de escaneo: {0}", "扫描错误：{0}"]),
        ("RefreshBusy", ["Refreshing…", "Обновляю…", "Aktualisierung…", "Actualisation…", "Actualizando…", "正在刷新…"]),
        ("RefreshDone", ["Updated", "Обновлено", "Aktualisiert", "Actualisé", "Actualizado", "已更新"]),
        ("TrayMenuShowHide", ["Show / hide", "Показать / скрыть", "Anzeigen / ausblenden", "Afficher / masquer", "Mostrar / ocultar", "显示 / 隐藏"]),
        ("TrayMenuRefresh", ["Refresh", "Обновить", "Aktualisieren", "Actualiser", "Actualizar", "刷新"]),
        ("TrayMenuExit", ["Exit", "Выход", "Beenden", "Quitter", "Salir", "退出"]),
        ("TooltipSettings", ["Settings", "Настройки", "Einstellungen", "Paramètres", "Configuración", "设置"]),
        ("TooltipRefreshNow", ["Refresh now", "Обновить сейчас", "Jetzt aktualisieren", "Actualiser maintenant", "Actualizar ahora", "立即刷新"]),
        ("TooltipHideToTray", ["Minimize to tray", "Свернуть в трей", "In den Infobereich minimieren", "Réduire dans la zone de notification", "Minimizar a la bandeja", "最小化到托盘"]),
        ("Ignore", ["Ignore", "Игнорировать", "Ignorieren", "Ignorer", "Ignorar", "忽略"]),
        ("NoDevices", ["No devices", "Нет устройств", "Keine Geräte", "Aucun appareil", "Sin dispositivos", "没有设备"]),
        ("NoDevicesFound", ["No devices found", "Устройства не найдены", "Keine Geräte gefunden", "Aucun appareil trouvé", "No se encontraron dispositivos", "未找到设备"]),
        ("SettingsTitle", ["Settings", "Настройки", "Einstellungen", "Paramètres", "Configuración", "设置"]),
        ("SettingsWindowTitle", ["GearHub — settings", "GearHub — настройки", "GearHub — Einstellungen", "GearHub — paramètres", "GearHub — configuración", "GearHub — 设置"]),
        ("SettingsClose", ["Close settings", "Закрыть настройки", "Einstellungen schließen", "Fermer les paramètres", "Cerrar la configuración", "关闭设置"]),
        ("SectionDisplay", ["Display", "Отображение", "Anzeige", "Affichage", "Visualización", "显示"]),
        ("ModeMax", ["Max", "Макс", "Max", "Max", "Máx", "最大"]),
        ("ModeMin", ["Min", "Мин", "Min", "Min", "Mín", "最小"]),
        ("AlwaysOnTop", ["Always on top", "Поверх всех окон", "Immer im Vordergrund", "Toujours au premier plan", "Siempre visible", "总在最前"]),
        ("SectionPosition", ["Position", "Расположение", "Position", "Position", "Posición", "位置"]),
        ("AnchorTopLeft", ["Top left", "Сверху слева", "Oben links", "En haut à gauche", "Arriba a la izquierda", "左上"]),
        ("AnchorTopRight", ["Top right", "Сверху справа", "Oben rechts", "En haut à droite", "Arriba a la derecha", "右上"]),
        ("AnchorBottomLeft", ["Bottom left", "Снизу слева", "Unten links", "En bas à gauche", "Abajo a la izquierda", "左下"]),
        ("AnchorBottomRight", ["Bottom right", "Снизу справа", "Unten rechts", "En bas à droite", "Abajo a la derecha", "右下"]),
        ("AnchorFree", ["Free (drag anywhere)", "Свободно (перетаскиванием)", "Frei (verschiebbar)", "Libre (déplaçable)", "Libre (arrastrable)", "自由（可拖动）"]),
        ("FlyoutTitle", ["Battery status", "Заряд устройств", "Akkustatus", "État des batteries", "Estado de las baterías", "设备电量"]),
        ("DeviceSource", ["Source: {0}", "Источник: {0}", "Quelle: {0}", "Source : {0}", "Origen: {0}", "来源：{0}"]),
        ("DeviceCharge", ["Charge: {0}", "Заряд: {0}", "Ladung: {0}", "Charge : {0}", "Carga: {0}", "电量：{0}"]),
        ("DeviceLastSeen", ["Last seen: {0}", "Последний контакт: {0}", "Zuletzt gesehen: {0}", "Vu pour la dernière fois : {0}", "Visto por última vez: {0}", "上次见到：{0}"]),
        ("DeviceConnected", ["Connected", "Подключено", "Verbunden", "Connecté", "Conectado", "已连接"]),
        ("DeviceAttention", ["Recently offline or problem", "Недавно отключено или проблема", "Kürzlich offline oder Problem", "Récemment déconnecté ou un problème", "Recientemente desconectado o con problema", "最近离线或出现问题"]),
        ("DeviceLost", ["Offline", "Давно отключено", "Offline", "Hors ligne", "Desconectado", "离线"]),
        ("AgoJustNow", ["just now", "только что", "gerade eben", "à l’instant", "ahora mismo", "刚刚"]),
        ("AgoMinutes", ["{0} min ago", "{0} мин назад", "vor {0} Min.", "il y a {0} min", "hace {0} min", "{0} 分钟前"]),
        ("AgoHours", ["{0} h ago", "{0} ч назад", "vor {0} Std.", "il y a {0} h", "hace {0} h", "{0} 小时前"]),
        ("AgoDays", ["{0} d ago", "{0} дн назад", "vor {0} Tagen", "il y a {0} j", "hace {0} d", "{0} 天前"]),
        ("BluetoothDevice", ["Bluetooth device", "Bluetooth-устройство", "Bluetooth-Gerät", "Appareil Bluetooth", "Dispositivo Bluetooth", "蓝牙设备"]),
        ("BatteryReadFailed", ["Could not read battery", "Не удалось прочитать заряд", "Akku konnte nicht gelesen werden", "Impossible de lire la batterie", "No se pudo leer la batería", "无法读取电量"]),
        ("GattError", ["GATT error: {0}", "Ошибка GATT: {0}", "GATT-Fehler: {0}", "Erreur GATT : {0}", "Error GATT: {0}", "GATT 错误：{0}"]),
        ("TimeoutNoReply", ["timeout: no reply", "таймаут: ответа нет", "Zeitüberschreitung: keine Antwort", "délai dépassé : aucune réponse", "tiempo agotado: sin respuesta", "超时：没有回复"]),
        ("DeviceAsleep", ["device is asleep", "устройство спит", "Gerät schläft", "appareil en veille", "el dispositivo está inactivo", "设备休眠中"]),
        ("ChargeUnreadable", ["battery not readable", "заряд не читается", "Akku nicht lesbar", "batterie illisible", "batería ilegible", "电量不可读"]),
        ("NotConnected", ["not connected", "не на связи", "nicht verbunden", "non connecté", "sin conexión", "未连接"]),
        ("ApproxByDongle", ["≈ from Lightspeed dongle", "≈ по данным донгла Lightspeed", "≈ laut Lightspeed-Dongle", "≈ d’après le dongle Lightspeed", "≈ según el receptor Lightspeed", "≈ 来自 Lightspeed 接收器"]),
        ("LogitechMouse", ["Logitech mouse", "Logitech-мышь", "Logitech-Maus", "Souris Logitech", "Ratón Logitech", "罗技鼠标"]),
        ("LogitechKeyboard", ["Logitech keyboard", "Logitech-клавиатура", "Logitech-Tastatur", "Clavier Logitech", "Teclado Logitech", "罗技键盘"]),
        ("LogitechGamepad", ["Logitech gamepad", "Logitech-геймпад", "Logitech-Gamepad", "Manette Logitech", "Mando Logitech", "罗技手柄"]),
        ("LogitechHeadset", ["Logitech headset", "Logitech-наушники", "Logitech-Headset", "Casque Logitech", "Auriculares Logitech", "罗技耳机"]),
        ("ChargingNotification", ["Charging (notification)", "Заряжается (нотификация)", "Wird geladen (Benachrichtigung)", "En charge (notification)", "Cargando (notificación)", "充电中（通知）"]),
        ("ByNotification", ["from notification", "по нотификации", "laut Benachrichtigung", "d’après la notification", "según notificación", "来自通知"]),
        ("ViaNotification", ["notification", "нотификация", "Benachrichtigung", "notification", "notificación", "通知"]),
        ("Charging", ["Charging", "Заряжается", "Wird geladen", "En charge", "Cargando", "充电中"]),
        ("ApproxByVoltage", ["≈ by voltage", "≈ по напряжению", "≈ laut Spannung", "≈ selon la tension", "≈ por voltaje", "≈ 根据电压"]),
        ("ChargeComplete", ["Charge complete", "Заряд завершён", "Ladevorgang abgeschlossen", "Charge terminée", "Carga completa", "充电完成"]),
        ("SlowCharging", ["Slow charging", "Медленная зарядка", "Langsames Laden", "Charge lente", "Carga lenta", "缓慢充电"]),
        ("BatteryProblem", ["Battery problem", "Проблема с батареей", "Akkuproblem", "Problème de batterie", "Problema de batería", "电池问题"]),
        ("BatteryOverheat", ["Battery overheating", "Перегрев батареи", "Akku überhitzt", "Surchauffe de la batterie", "Sobrecalentamiento de la batería", "电池过热"]),
        ("ChargingError", ["Charging error", "Ошибка зарядки", "Ladefehler", "Erreur de charge", "Error de carga", "充电错误"]),
        ("ApproxByDevice", ["≈ from device", "≈ по данным устройства", "≈ laut Gerät", "≈ d’après l’appareil", "≈ según el dispositivo", "≈ 来自设备"]),
        ("XboxGamepad", ["Xbox gamepad (slot {0})", "Xbox-геймпад (слот {0})", "Xbox-Gamepad (Slot {0})", "Manette Xbox (port {0})", "Mando Xbox (ranura {0})", "Xbox 手柄（插槽 {0}）"]),
        ("UsbPower", ["USB power", "Питание от USB", "USB-Stromversorgung", "Alimentation USB", "Alimentación por USB", "USB 供电"]),
        ("BatteriesAA", ["AA batteries", "Батарейки AA", "AA-Batterien", "Piles AA", "Pilas AA", "AA 电池"]),
        ("RechargeableBattery", ["Rechargeable battery", "Аккумулятор", "Akku", "Batterie rechargeable", "Batería recargable", "可充电电池"]),
    ];

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Tables = BuildTables();

    private static readonly IReadOnlyDictionary<string, string> English = Tables["en"];

    private static IReadOnlyDictionary<string, string> _current = English;

    /// <summary>Current language code: en, ru, de, fr, es, zh.</summary>
    public static string Language { get; private set; } = "en";

    /// <summary>Enables the language by code (e.g. "ru-RU"); unknown language falls back to English.</summary>
    public static void Use(string? languageCode)
    {
        Language = Normalize(languageCode);
        _current = Tables[Language];
    }

    /// <summary>String by key; if no translation exists — the English variant, then the key itself.</summary>
    public static string Get(string key) => _current.TryGetValue(key, out var value)
        ? value
        : English.TryGetValue(key, out var fallback) ? fallback : key;

    /// <summary>String with substitutions (e.g. "{0} devices · online: {1}").</summary>
    public static string Format(string key, params object?[] args) => string.Format(Get(key), args);

    private static string Normalize(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return "en";
        }

        var primary = languageCode.Trim().ToLowerInvariant().Split('-', '_')[0];
        return Tables.ContainsKey(primary) ? primary : "en";
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> BuildTables()
    {
        var tables = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        for (var language = 0; language < LanguageCodes.Length; language++)
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in Rows)
            {
                table[row.Key] = row.Values[language];
            }

            tables[LanguageCodes[language]] = table;
        }

        return tables;
    }
}
