namespace GearHub.Core.Models;

/// <summary>Цветовой статус устройства в виджете.</summary>
public enum GearStatus
{
    /// <summary>Зелёный: устройство подключено сейчас.</summary>
    Online = 0,

    /// <summary>Жёлтый: недавно отключено или что-то не так (ошибка чтения заряда).</summary>
    Attention,

    /// <summary>Красный: давно отключено.</summary>
    Lost,
}
