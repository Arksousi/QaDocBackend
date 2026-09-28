namespace QaDocBackend.Data;

/// <summary>
/// How many unfinished tickets someone holds, measured against their ticket limit. One definition
/// shared by the Users Dashboard, both "Assigned To" lists and the scoreboard, so the number never
/// differs between them.
/// </summary>
public static class Workload
{
    /// <summary>
    /// A SQL subquery counting tickets assigned to <paramref name="userIdColumn"/> that are not Closed,
    /// in every real project. A shared ticket counts in full for each person on it. Demo projects are
    /// left out: their sample tickets are assigned to real colleagues and are nobody's actual work.
    /// </summary>
    public static string OpenTicketsOf(string userIdColumn) => $@"
        (SELECT COUNT(*) FROM TicketAssignees wta
         JOIN Tickets wt  ON wt.TicketId = wta.TicketId
         JOIN Projects wp ON wp.ProjectId = wt.ProjectId
         WHERE wta.UserId = {userIdColumn} AND wt.State <> 'Closed' AND NOT wp.IsDemo)";
}
