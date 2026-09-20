namespace Xur.Domain;
public record OsDeployment(string Version,string Digest,string Image,bool DownloadOnly);
public record OsUpdateOperation(string Id,string Action,string Stage,double Updated,string Message);
public record OsUpdateStatus(OsDeployment? Current,OsDeployment? Pending,OsDeployment? Previous,
    OsDeployment? Available,bool RollbackQueued,bool Automatic,bool Busy,OsUpdateOperation? Operation,string Logs);
public record OsUpdateAction(string Action);
