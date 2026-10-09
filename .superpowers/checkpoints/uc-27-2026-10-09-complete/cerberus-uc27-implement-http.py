from pathlib import Path
w=Path('/tmp/cerberus-uc27-worktree');log=Path('/tmp/cerberus-uc27-http-red.log').read_text();assert 'Failed!' in log and ('MethodNotAllowed' in log or 'Actual:   405' in log)
p=w/'src/Presentation/ArturRios.Cerberus.WebApi/Controllers/FoldersController.cs';s=p.read_text();assert '[HttpDelete("{id}")]' not in s
source=(w/'src/Presentation/ArturRios.Cerberus.WebApi/Controllers/RecordsController.cs').read_text();start=source.index('    [HttpDelete("{id}")]');end=source.index('    [HttpPut("{id}")]',start);action=source[start:end].replace('Record','Folder').replace('recordId','folderId');anchor='    [HttpPut("{id}")]';assert s.count(anchor)==1;p.write_text(s.replace(anchor,action+anchor))
p=w/'src/Presentation/ArturRios.Cerberus.WebApi/Startup.cs';s=p.read_text();assert 'IFolderTrashStore' not in s
for a,b in [('builder.Services.AddScoped<IRecordTrashStore, RecordTrashStore>();','builder.Services.AddScoped<IFolderTrashStore, FolderTrashStore>();'),('builder.Services.AddScoped<IValidator<DeleteRecordCommand>, DeleteRecordValidator>();','builder.Services.AddScoped<IValidator<DeleteFolderCommand>, DeleteFolderValidator>();'),('builder.Services.AddScoped<ICommandHandlerAsync<DeleteRecordCommand, DeleteRecordOutput>, DeleteRecordHandler>();','builder.Services.AddScoped<ICommandHandlerAsync<DeleteFolderCommand, DeleteFolderOutput>, DeleteFolderHandler>();')]:
 assert a in s,a;s=s.replace(a,a+'\n        '+b)
p.write_text(s);print('Added strict DELETE and three explicit registrations after genuine missing-route RED.')
