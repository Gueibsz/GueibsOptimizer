using System.IO;
using System.Runtime.InteropServices;

namespace GueibsOptimizer.Core;

public sealed record ScheduledTaskInfo(string Path, string Name, string Command, bool Enabled, bool HasLogonTrigger);

/// <summary>
/// Acesso ao Agendador de Tarefas via COM (Schedule.Service). Evita parsear a saída
/// localizada do schtasks. Todas as falhas viram retorno vazio/false.
/// </summary>
public static class TaskSchedulerCom
{
    private const int TaskTriggerLogon = 9;
    private const int TaskActionExec = 0;

    private static dynamic? Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null) return null;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    /// <summary>null se a tarefa não existir ou não puder ser lida.</summary>
    public static bool? IsEnabled(string folderPath, string taskName)
    {
        try
        {
            var service = Connect();
            if (service is null) return null;
            dynamic task = service.GetFolder(folderPath).GetTask(taskName);
            return (bool)task.Enabled;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or FileNotFoundException)
        {
            return null;
        }
    }

    public static bool SetEnabled(string folderPath, string taskName, bool enabled)
    {
        try
        {
            var service = Connect();
            if (service is null) return false;
            dynamic task = service.GetFolder(folderPath).GetTask(taskName);
            task.Enabled = enabled;
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Apaga a tarefa e, se a pasta dela ficar vazia (e não for a raiz), a pasta também. Retorna se apagou.</summary>
    public static bool DeleteTask(string folderPath, string taskName)
    {
        try
        {
            var service = Connect();
            if (service is null) return false;
            dynamic folder = service.GetFolder(folderPath);
            folder.DeleteTask(taskName, 0);
            if (folderPath != "\\" && (int)folder.GetTasks(1).Count == 0 && (int)folder.GetFolders(0).Count == 0)
            {
                try { service.GetFolder("\\").DeleteFolder(folderPath.TrimStart('\\'), 0); }
                catch (COMException) { } // pasta vazia sobrando não atrapalha
            }
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Lista tarefas (recursivo) que têm gatilho de logon.</summary>
    public static IReadOnlyList<ScheduledTaskInfo> ListLogonTasks()
    {
        var result = new List<ScheduledTaskInfo>();
        try
        {
            var service = Connect();
            if (service is null) return result;
            Walk(service.GetFolder("\\"), result);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
        }
        return result;
    }

    private static void Walk(dynamic folder, List<ScheduledTaskInfo> result)
    {
        try
        {
            foreach (dynamic task in folder.GetTasks(1)) // 1 = incluir ocultas
            {
                try
                {
                    dynamic def = task.Definition;
                    bool logon = false;
                    foreach (dynamic trigger in def.Triggers)
                        if ((int)trigger.Type == TaskTriggerLogon) { logon = true; break; }
                    if (!logon) continue;

                    string command = string.Empty;
                    foreach (dynamic action in def.Actions)
                    {
                        if ((int)action.Type != TaskActionExec) continue;
                        command = $"{(string)action.Path} {(string?)action.Arguments ?? string.Empty}".Trim();
                        break;
                    }
                    result.Add(new ScheduledTaskInfo((string)folder.Path, (string)task.Name, command, (bool)task.Enabled, true));
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                }
            }
            foreach (dynamic sub in folder.GetFolders(0))
                Walk(sub, result);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
        }
    }
}
