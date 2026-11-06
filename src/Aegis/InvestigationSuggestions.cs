namespace Aegis;

public static class InvestigationSuggestions
{
    public static string? GetQuestion(string? suggestion) => suggestion switch
    {
        "Performance" => "What may explain why this computer feels slow? Investigate current system performance and resource usage.",
        "SystemErrors" => "What recent Windows errors may be relevant to this computer's reliability?",
        "SystemInformation" => "What Windows system information is available for this computer?",
        "SystemHealth" => "Give a general overview of the available Windows system information, current resource usage, and recent error evidence, noting any limitations.",
        "TopProcesses" => "Which accessible processes have the highest sampled CPU use and memory working sets, and what are the limitations of this snapshot?",
        "Reliability" => "What can the available recent Windows error evidence tell me about stability and reliability, and what are its limitations?",
        _ => null
    };
}
