using System.Globalization;
using System.Text;

namespace Arr411.Core.Update;

/// <summary>A step of an update that failed, with what to tell the user (in French); the details are for the log only.</summary>
internal sealed class UpdateFailure : Exception
{
    public UpdateFailure(string message, string? detail = null, bool final = false)
        : base(message)
    {
        Detail = detail;
        Final = final;
    }

    public string? Detail { get; }

    /// <summary>Trying again would give the same answer (the bytes do not match what the release says): not before the next start.</summary>
    public bool Final { get; }
}

/// <summary>What the user is told, in French, and the few text rules that go with it.</summary>
internal static class UpdateText
{
    public const string CannotReach = "Impossible de joindre GitHub. Vérifiez la connexion Internet.";
    public const string NoAnswer = "GitHub ne répond pas. Réessayez plus tard.";
    public const string RateLimited = "GitHub limite temporairement les requêtes. Réessayez plus tard.";
    public const string UnreadableAnswer = "La réponse de GitHub est illisible.";
    public const string NoRelease = "Aucune version n’est publiée sur GitHub.";
    public const string Refused = "Adresse refusée par sécurité : la mise à jour doit venir de GitHub.";
    public const string TooManyRedirects = "Trop de redirections : téléchargement abandonné.";
    public const string DownloadFailed = "Le téléchargement de la mise à jour a échoué.";
    public const string Stalled = "Le téléchargement s’est interrompu. Réessayez plus tard.";
    public const string WrongSize = "Le fichier téléchargé n’a pas la taille annoncée : mise à jour abandonnée.";
    public const string WrongHash = "La vérification d’intégrité a échoué : le fichier téléchargé est refusé.";
    public const string SumsUnusable = "Le fichier SHA256SUMS.txt de la version ne permet pas de vérifier la mise à jour.";
    public const string NothingToVerify = "La version ne publie aucune empreinte : elle ne peut pas être installée automatiquement.";
    public const string CannotInstallHere = "L’installation automatique n’est pas possible ici : téléchargez la nouvelle version depuis sa page.";
    public const string CannotSave = "La mise à jour n’a pas pu être enregistrée (disque plein ou dossier inaccessible).";
    public const string StagedChanged = "Le fichier de mise à jour a changé depuis son téléchargement : il sera téléchargé de nouveau.";
    public const string LaunchFailed = "Impossible de lancer l’installation de la mise à jour.";
    public const string NotClosed = "Arr411 ne s’est pas fermé à temps : la mise à jour n’a pas été installée.";
    public const string Unexpected = "La mise à jour a rencontré un problème inattendu.";
    public const string BadApplyArguments = "L’installation de la mise à jour a reçu des paramètres invalides.";

    public const string AvailableTitle = "Mise à jour disponible";
    public const string ReadyTitle = "Mise à jour prête";
    public const string FailedTitle = "Mise à jour impossible";
    public const string UpdatedTitle = "Arr411";

    private const int MaxMessageChars = 300;

    public static string HttpError(int status) => $"GitHub a répondu par une erreur (HTTP {status.ToString(CultureInfo.InvariantCulture)}).";

    public static string ApplyFailed(string version) =>
        $"La mise à jour vers la version {version} n’a pas pu être installée (fichier verrouillé ou dossier protégé). Votre version actuelle est conservée.";

    public static string Updated(string version) => $"Arr411 a été mis à jour en {version}";

    public static string Available(string version) => $"Arr411 {version} est disponible. Ouvrez les réglages pour l’installer.";

    public static string Ready(string version) => $"Arr411 {version} est prêt : il s’installera à la fermeture ou au prochain démarrage.";

    /// <summary>The first <paramref name="max"/> characters, without cutting a surrogate pair in two.</summary>
    public static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";

        var length = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..length];
    }

    /// <summary>A message read from a file the applier wrote: one line of printable text, short, never markup that a page would take for something else.</summary>
    public static string CleanMessage(string? text)
    {
        var clean = new StringBuilder();
        foreach (var c in text ?? "")
        {
            if (c is '<' or '>') clean.Append(' ');
            else if (!char.IsControl(c)) clean.Append(c);
            else if (c is '\r' or '\n' or '\t') clean.Append(' ');
        }

        return Truncate(clean.ToString().Trim(), MaxMessageChars);
    }
}
