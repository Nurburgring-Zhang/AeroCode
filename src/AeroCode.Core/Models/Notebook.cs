using System;
using System.Collections.Generic;

namespace AeroCode.Core.Models;

/// <summary>
/// 笔记本：用于组织笔记的容器。Notebook 之间可以嵌套（ParentId 自引用）。
/// </summary>
public class Notebook
{
    public long Id { get; set; }

    public long? ParentId { get; set; }
    public Notebook? Parent { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Notebook> Children { get; set; } = new List<Notebook>();

    public ICollection<Note> Notes { get; set; } = new List<Note>();
}
