from pathlib import Path
p=Path('src/Presentation/ArturRios.Cerberus.WebApi/Controllers/RecordsController.cs');s=p.read_text();marker='    [HttpDelete("{id}")]';a=s.index(marker);b=s.index('    [HttpPut("{id}")]',a);route=s[a:b].replace('[HttpDelete("{id}")]','[HttpPut("{id}/folder")]').replace('DeleteRecord','MoveRecord').replace('>>> Delete(', '>>> Move(');assert route.count('public async Task')==1 and '>>> Move(' in route;s=s[:a]+route+s[a:];p.write_text(s)
p=Path('src/Presentation/ArturRios.Cerberus.WebApi/Startup.cs');s=p.read_text();a=s.index('        builder.Services.AddScoped<IRecordTrashStore');s=s[:a]+'''        builder.Services.AddScoped<IRecordMoveStore, RecordMoveStore>();
        builder.Services.AddScoped<IValidator<MoveRecordCommand>, MoveRecordValidator>();
        builder.Services.AddScoped<ICommandHandlerAsync<MoveRecordCommand, MoveRecordOutput>, MoveRecordHandler>();
'''+s[a:];p.write_text(s)
