namespace ArturRios.Cerberus.Domain.Trash;
public sealed record ProfileAssociationSnapshot(Guid[] RecordIds, Guid[] FolderIds, Guid[] CollectionIds);
