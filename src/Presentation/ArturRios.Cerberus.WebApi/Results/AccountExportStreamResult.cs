using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Query.Accounts;
using ArturRios.Output;
using Microsoft.AspNetCore.Mvc;

namespace ArturRios.Cerberus.WebApi.Streaming;

public sealed class AccountExportStreamResult(IAccountExportLease lease):IActionResult
{
    public const string Format="cerberus-account-export-v1";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http=context.HttpContext;var response=http.Response;var ct=http.RequestAborted;
        IAsyncEnumerator<AccountExportEntry>? reader=null;var ownsLease=true;
        try
        {
            reader=lease.ReadAsync(ct).GetAsyncEnumerator(ct);
            if(!await reader.MoveNextAsync())throw new AccountExportException("persistence_unavailable");
            // Validate/serialize the first entry before committing status/attachment headers.
            var encoded=JsonSerializer.SerializeToUtf8Bytes<AccountExportEntry>(reader.Current,Json);
            var prefix=Encoding.UTF8.GetBytes("{\"format\":\""+Format+"\",\"header\":"+JsonSerializer.Serialize(lease.Header,Json)+",\"entries\":[");
            ct.ThrowIfCancellationRequested();response.StatusCode=StatusCodes.Status200OK;response.ContentType="application/json; charset=utf-8";
            response.Headers.CacheControl="no-store";response.Headers.Remove("ETag");response.Headers.Remove("Last-Modified");
            response.Headers.ContentDisposition="attachment; filename=\"cerberus-account-export.json\"";
            await Write(response,prefix,ct);long count=0;
            do
            {
                ct.ThrowIfCancellationRequested();if(count>0)response.BodyWriter.Write(","u8);
                await Write(response,encoded,ct);count=checked(count+1);if(count>ProtocolBinary.MaxInteger)throw new AccountExportException("persistence_unavailable");
                if(!await reader.MoveNextAsync())break;
                encoded=JsonSerializer.SerializeToUtf8Bytes<AccountExportEntry>(reader.Current,Json);
            }while(true);
            // Both cleanup boundaries can fail: neither may follow a success footer.
            await reader.DisposeAsync();reader=null;
            await lease.DisposeAsync();ownsLease=false;
            ct.ThrowIfCancellationRequested();
            await Write(response,Encoding.UTF8.GetBytes("],\"entryCount\":"+count.ToString(CultureInfo.InvariantCulture)+",\"complete\":true}"),ct);
        }
        catch(Exception exception)
        {
            var code=exception is AccountExportException e&&AccountExportMessages.StatusCodes.ContainsKey(e.Error)?e.Error:"persistence_unavailable";
            http.RequestServices.GetRequiredService<ILogger<AccountExportStreamResult>>().LogWarning("Account export failed. Code={Code}",code);
            if(response.HasStarted||ct.IsCancellationRequested){http.Abort();return;}
            response.Clear();response.StatusCode=AccountExportMessages.StatusCodes[code];
            await response.WriteAsJsonAsync(ProcessOutput.New.WithError(code),ct);
        }
        finally
        {
            if(reader is not null){try{await reader.DisposeAsync();}catch(Exception){/* Preserve failure and continue owner cleanup. */}}
            if(ownsLease){try{await lease.DisposeAsync();}catch(Exception){/* The safe error/aborted response is already selected. */}}
        }
    }
    private static async Task Write(HttpResponse response,ReadOnlyMemory<byte> bytes,CancellationToken ct)
    {
        var flush=await response.BodyWriter.WriteAsync(bytes,ct);
        if(flush.IsCanceled||flush.IsCompleted)throw new IOException("Account export is unavailable.");
    }
}
