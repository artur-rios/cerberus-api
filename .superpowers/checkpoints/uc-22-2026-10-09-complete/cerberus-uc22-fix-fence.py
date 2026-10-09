from pathlib import Path
p=Path('src/Infrastructure/ArturRios.Cerberus.Data/Erasure/RecordErasure.cs');s=p.read_text();s=s.replace('using Microsoft.EntityFrameworkCore;', 'using Microsoft.EntityFrameworkCore;\nusing Npgsql;')
a=s.index('        await Fence();\n        var operations');b=s.index('\n    internal static async Task RequireClaimAsync',a)
s=s[:a]+'''        async Task Delete(string sql,params object[] parameters)
        {
            await Fence();
            // Expiry is tested by this mutation's statement timestamp, not a
            // preceding SELECT. The row lock also fences concurrent token theft.
            var guard=" AND EXISTS(SELECT FROM cerberus.terminal_erasure terminal WHERE terminal.resource_kind='record' AND terminal.resource_id=@terminal_id)";
            var args=parameters.Append<object>(new NpgsqlParameter("terminal_id",id)).ToList();
            if(claim is not null)
            {
                guard+=" AND EXISTS(SELECT FROM cerberus.retention_work_item authority WHERE authority.public_id=@work_id AND authority.claim_token=@claim_token AND authority.operation_key=@work_key AND authority.completed_at IS NULL AND authority.claim_expires_at>statement_timestamp())";
                args.AddRange([new NpgsqlParameter("work_id",claim.WorkId),new NpgsqlParameter("claim_token",claim.Token),new NpgsqlParameter("work_key",claim.OperationKey)]);
            }
            var statement=sql+guard;
            await db.Database.ExecuteSqlRawAsync(statement,args,ct);
            // A fenced zero-row idempotent delete must not acknowledge a lost claim.
            await Fence();
        }
        await Fence();
        var operations = await db.TrashOperations.Where(x => x.RootResourceKind == "record" && x.RootResourceId == id)
            .Select(x => new { x.Id, x.PublicId }).ToListAsync(ct);
        await Delete("DELETE FROM cerberus.record WHERE public_id=@id",new NpgsqlParameter("id",id));
        // Typed record associations cascade from the physical record FK.
        await Delete("DELETE FROM cerberus.trash_entry WHERE resource_kind='record' AND resource_id=@id",new NpgsqlParameter("id",id));
        foreach (var operation in operations)
        {
            if (await db.TrashEntries.AnyAsync(x => x.OperationId == operation.Id, ct)) continue;
            await Delete("DELETE FROM cerberus.retention_work_item WHERE operation_key=@trash_key AND NOT EXISTS(SELECT FROM cerberus.trash_entry e WHERE e.operation_id=@operation_id)",
                new NpgsqlParameter("trash_key","trash/"+operation.PublicId),new NpgsqlParameter("operation_id",operation.Id));
            await Delete("DELETE FROM cerberus.trash_operation WHERE id=@operation_id AND NOT EXISTS(SELECT FROM cerberus.trash_entry e WHERE e.operation_id=@operation_id)",new NpgsqlParameter("operation_id",operation.Id));
        }
    }
''' +s[b:];p.write_text(s)
p=Path('src/Infrastructure/ArturRios.Cerberus.Data/Protection/VaultProtectionChangeStore.cs');s=p.read_text().replace('db.Records.Where(x=>x.AccountId==a.Id).ToListAsync(cancellationToken)', 'db.Records.Where(x=>x.AccountId==a.Id && !db.TerminalErasures.Any(e=>e.ResourceKind=="record" && e.ResourceId==x.PublicId)).ToListAsync(cancellationToken)');p.write_text(s)
