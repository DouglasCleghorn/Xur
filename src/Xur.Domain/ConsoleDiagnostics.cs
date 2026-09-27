namespace Xur.Domain;

public record ConsoleDiagnosticOption(int Id,string Label,bool Enabled);
public record ConsoleDiagnosticSnapshot(string Revision,string Screen,string Title,string Body,
    ConsoleDiagnosticOption[] Options,int Selected,bool AcceptsText,bool Secret,string? InputValue);
// Option IDs come from the returned screen. ConfirmErase is additional explicit consent,
// never a replacement for the setup plan's disk identity, digest and expiry checks.
public record ConsoleDiagnosticAction(string Revision,int? Option=null,string? Text=null,bool ConfirmErase=false);
