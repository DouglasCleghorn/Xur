namespace Xur.Domain;
public record OsDeployment(string Version,string Digest,string Image,bool DownloadOnly);
public record OsUpdateOperation(string Id,string Action,string Stage,double Updated,string Message);
public record OsUpdateStatus(OsDeployment? Current,OsDeployment? Pending,OsDeployment? Previous,
    OsDeployment? Available,bool RollbackQueued,bool Automatic,bool Busy,OsUpdateOperation? Operation,string Logs,
    OsUpdateSchedule? Schedule=null,OsUpdateWindow? Window=null,double? NextWindow=null,string Timezone="");
// Days use Monday=0 through Sunday=6, matching the host scheduler.
public record OsUpdateSchedule(string Time,int[] Days,int WarningMinutes=15);
public record OsUpdateWindow(string Id,double ScheduledAt,double StartsAt,string Version,string State);
public record OsUpdateAction(string Action,OsUpdateSchedule? Schedule=null,string? WindowId=null);
