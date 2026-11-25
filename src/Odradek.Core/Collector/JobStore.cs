using System.Text.RegularExpressions;
using Dapper;
using Odradek.Core.Data;

namespace Odradek.Core.Collector;

/// <summary>Gravação de vagas coletadas, compartilhada pelo ciclo de coleta e pela importação do LinkedIn.</summary>
public static partial class JobStore
{
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();

    /// <summary>Grava a vaga; devolve o id, ou <c>null</c> se o fingerprint já existia.</summary>
    public static long? Insert(Database db, CollectedJob job)
    {
        var (language, confidence) = LanguageDetector.Detect(job.Title + "\n" + Tags().Replace(job.Description, " "));
        using var connection = db.Open();
        return connection.ExecuteScalar<long?>("""
            INSERT INTO jobs(source,source_id,title,company,location,description,url,posted_at,first_seen_at,fingerprint,language,language_confidence,search_filter_id,search_remote)
            VALUES(@Source,@SourceId,@Title,@Company,@Location,@Description,@Url,@PostedAt,@Now,@Fingerprint,@Language,@Confidence,@FilterId,@SearchRemote)
            ON CONFLICT(fingerprint) DO NOTHING RETURNING id
            """, new
        {
            job.Source, job.SourceId, job.Title, job.Company, job.Location, job.Description, job.Url, job.PostedAt, Now = Clock.NowIso(),
            Fingerprint = Odradek.Core.Fingerprint.Of(job.Url, job.Company, job.Title, job.Location), Language = language, Confidence = confidence,
            job.FilterId, job.SearchRemote,
        });
    }
}
