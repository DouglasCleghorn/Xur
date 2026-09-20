namespace Xur.Domain;
public record UpdateAllResult(string Name,string Stage,string Message);
public record UpdateAllOperation(string Id,string Stage,string Message,UpdateAllResult[] Results,double Updated);
public record UpdateAllStatus(bool Busy,UpdateAllOperation? Operation);
