namespace ArturRios.Cerberus.Domain.Trash;
public sealed record FolderAssociationSnapshot(Guid? ParentFolderId,Guid[] ProfileIds,Guid[] CollectionIds);
