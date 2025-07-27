using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Сервис для сбора и отправки метрик производительности
/// </summary>
public interface IMetricsService
{
    /// <summary>
    /// Измерение времени выполнения операции
    /// </summary>
    void Measure(string operationName, Action operation);
    
    /// <summary>
    /// Измерение времени выполнения операции с тегами
    /// </summary>
    void Measure(string operationName, Action operation, Dictionary<string, string>? tags = null);
    
    /// <summary>
    /// Измерение времени выполнения операции с возвратом значения
    /// </summary>
    T Measure<T>(string operationName, Func<T> operation);
    
    /// <summary>
    /// Измерение времени выполнения операции с возвратом значения и тегами
    /// </summary>
    T Measure<T>(string operationName, Func<T> operation, Dictionary<string, string>? tags = null);
    
    /// <summary>
    /// Измерение времени выполнения асинхронной операции (оставляем для реальных async операций)
    /// </summary>
    Task<T> MeasureAsync<T>(string operationName, Func<Task<T>> operation);
    
    /// <summary>
    /// Измерение времени выполнения асинхронной операции с тегами
    /// </summary>
    Task<T> MeasureAsync<T>(string operationName, Func<Task<T>> operation, Dictionary<string, string>? tags = null);
    
    /// <summary>
    /// Увеличение счетчика операций
    /// </summary>
    void IncrementCounter(string counterName, Dictionary<string, string>? tags = null);
    
    /// <summary>
    /// Запись значения в гистограмму (для времени ответа)
    /// </summary>
    void RecordHistogram(string metricName, double value, Dictionary<string, string>? tags = null);
    
    /// <summary>
    /// Установка значения gauge метрики (для текущих значений)
    /// </summary>
    void SetGauge(string gaugeName, double value, Dictionary<string, string>? tags = null);
    
    /// <summary>
    /// Получение среднего значения метрики
    /// </summary>
    Task<double> GetAverageAsync(string metricName);
    
    /// <summary>
    /// Получение значения счетчика
    /// </summary>
    Task<long> GetCounterAsync(string counterName);
    
    /// <summary>
    /// Получение коэффициента (rate) метрики
    /// </summary>
    Task<double> GetRateAsync(string metricName);
    
    /// <summary>
    /// Получение всех метрик системы
    /// </summary>
    Task<Dictionary<string, object>> GetMetricsAsync();
}
