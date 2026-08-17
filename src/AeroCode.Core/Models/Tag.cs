using System;
using System.Collections.Generic;

namespace AeroCode.Core.Models;

/// <summary>
/// 标签：通过 NoteTag 关联表与 Note 多对多。
/// </summary>
public class Tag
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Color { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<NoteTag> NoteTags { get; set; } = new List<NoteTag>();
}

/// <summary>
/// 笔记-标签关联实体。
/// </summary>
public class NoteTag
{
    public long NoteId { get; set; }
    public Note? Note { get; set; }

    public long TagId { get; set; }
    public Tag? Tag { get; set; }
}
