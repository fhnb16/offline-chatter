using System;

namespace OfflineChatter.Core.Chat;

/// <summary>Связь с эндпоинтом чата (облачный API или удалённый сервер).</summary>
public enum EndpointState
{
    /// <summary>Ещё не проверяли.</summary>
    Unknown,
    Checking,
    /// <summary>Адрес отвечает, ключ принят.</summary>
    Online,
    /// <summary>Адрес отвечает, но ключ не принят (401 / 403).</summary>
    Unauthorized,
    /// <summary>Нет связи: DNS, TLS, прокси, тайм-аут.</summary>
    Unreachable,
    /// <summary>Сервер ответил ошибкой (5xx, 429 и т. п.).</summary>
    Error,
    /// <summary>Включён офлайн-режим программы — запросы не отправляются.</summary>
    Offline,
}

/// <summary>Результат одной проверки связи.</summary>
public sealed class EndpointStatus
{
    public EndpointStatus(EndpointState state, int? httpStatus, double? latencyMs, string? error)
    {
        State = state;
        HttpStatus = httpStatus;
        LatencyMs = latencyMs;
        Error = error;
        CheckedAt = DateTime.Now;
    }

    public EndpointState State { get; }
    public int? HttpStatus { get; }

    /// <summary>Время до заголовков ответа, мс.</summary>
    public double? LatencyMs { get; }
    public string? Error { get; }
    public DateTime CheckedAt { get; }

    /// <summary>Состояние по итогу настоящего запроса чата — без отдельной проверки.</summary>
    public static EndpointStatus FromRequest(Exception? error, double? latencyMs)
    {
        if (error == null) return new EndpointStatus(EndpointState.Online, 200, latencyMs, null);
        if (error is Net.OfflineModeException) return new EndpointStatus(EndpointState.Offline, null, null, null);
        if (error is Net.HttpStatusException hs)
        {
            var code = (int)hs.Status;
            return new EndpointStatus(code is 401 or 403 ? EndpointState.Unauthorized : EndpointState.Error, code, null, hs.Body);
        }
        return new EndpointStatus(EndpointState.Unreachable, null, null, error.GetBaseException().Message);
    }
}
