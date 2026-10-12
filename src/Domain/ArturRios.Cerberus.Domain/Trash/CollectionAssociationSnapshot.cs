namespace ArturRios.Cerberus.Domain.Trash;

public sealed record CollectionAssociationSnapshot(Guid[] ProfileIds, Guid[] RecordIds, Guid[] FolderIds);
