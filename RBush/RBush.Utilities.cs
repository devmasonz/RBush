﻿using System.Collections.Immutable;

namespace RBush;

public partial class RBush<T>
{
	#region Sort Functions
	private static readonly IComparer<ISpatialData> s_compareMinX =
		Comparer<ISpatialData>.Create((x, y) => Comparer<double>.Default.Compare(x.Envelope.MinX, y.Envelope.MinX));
	private static readonly IComparer<ISpatialData> s_compareMinY =
		Comparer<ISpatialData>.Create((x, y) => Comparer<double>.Default.Compare(x.Envelope.MinY, y.Envelope.MinY));
	#endregion

	#region Search
	private List<ImmutableStack<ISpatialData>> DoPathSearch(in Envelope boundingBox)
	{
		if (!Root.Envelope.Intersects(boundingBox))
			return new List<ImmutableStack<ISpatialData>>();

		var intersections = new List<ImmutableStack<ISpatialData>>();
		var queue = new Queue<ImmutableStack<ISpatialData>>();
		queue.Enqueue(ImmutableStack<ISpatialData>.Empty.Push(Root));

		do
		{
			var current = queue.Dequeue();
			foreach (var c in (current.Peek() as Node).Items)
			{
				if (c.Envelope.Intersects(boundingBox))
				{
					if (c is T)
						intersections.Add(current.Push(c));
					else
						queue.Enqueue(current.Push(c));
				}
			}
		} while (queue.Count != 0);

		return intersections;
	}

	private List<T> DoSearch(in Envelope boundingBox)
	{
		if (!Root.Envelope.Intersects(boundingBox))
			return new List<T>();

		var intersections = new List<T>();
		var queue = new Queue<Node>();
		queue.Enqueue(Root);

		while (queue.Count != 0)
		{
			var item = queue.Dequeue();

			if (item.IsLeaf)
			{
				for (var index = 0; index < item.Items.Count; index++)
				{
					var leafChildItem = item.Items[index];
					if (leafChildItem.Envelope.Intersects(boundingBox))
						intersections.Add((T)leafChildItem);
				}
			}
			else
			{
				for (var index = 0; index < item.Items.Count; index++)
				{
					var childNode = item.Items[index];
					if (childNode.Envelope.Intersects(boundingBox))
						queue.Enqueue((Node)childNode);
				}
			}
		}

		return intersections;
	}
	#endregion

	#region Insert
	private List<Node> FindCoveringArea(in Envelope area, int depth)
	{
		var path = new List<Node>();
		var node = this.Root;
		var _area = area; //FIX CS1628

		while (true)
		{
			path.Add(node);
			if (node.IsLeaf || path.Count == depth) return path;

			node = node.Items
				.Select(c => new { EnlargedArea = c.Envelope.Extend(_area).Area, c.Envelope.Area, Node = c as Node, })
				.OrderBy(x => x.EnlargedArea)
				.ThenBy(x => x.Area)
				.Select(x => x.Node)
				.First();
		}
	}

	private void Insert(ISpatialData data, int depth)
	{
		var path = FindCoveringArea(data.Envelope, depth);

		var insertNode = path.Last();
		insertNode.Add(data);

		while (--depth >= 0)
		{
			if (path[depth].Items.Count > _maxEntries)
			{
				var newNode = SplitNode(path[depth]);
				if (depth == 0)
					SplitRoot(newNode);
				else
					path[depth - 1].Add(newNode);
			}
			else
				path[depth].ResetEnvelope();
		}
	}

	#region SplitNode
	private void SplitRoot(Node newNode) =>
		this.Root = new Node(new List<ISpatialData> { this.Root, newNode }, this.Root.Height + 1);

	private Node SplitNode(Node node)
	{
		SortChildren(node);

		var splitPoint = GetBestSplitIndex(node.Items);
		var newChildren = node.Items.Skip(splitPoint).ToList();
		node.RemoveRange(splitPoint, node.Items.Count - splitPoint);
		return new Node(newChildren, node.Height);
	}

	#region SortChildren
	private void SortChildren(Node node)
	{
		node.Items.Sort(s_compareMinX);
		var splitsByX = GetPotentialSplitMargins(node.Items);
		node.Items.Sort(s_compareMinY);
		var splitsByY = GetPotentialSplitMargins(node.Items);

		if (splitsByX < splitsByY)
			node.Items.Sort(s_compareMinX);
	}

	private double GetPotentialSplitMargins(List<ISpatialData> children) =>
		GetPotentialEnclosingMargins(children) +
		GetPotentialEnclosingMargins(children.AsEnumerable().Reverse().ToList());

	private double GetPotentialEnclosingMargins(List<ISpatialData> children)
	{
		var envelope = Envelope.EmptyBounds;
		var i = 0;
		for (; i < _minEntries; i++)
		{
			envelope = envelope.Extend(children[i].Envelope);
		}

		var totalMargin = envelope.Margin;
		for (; i < children.Count - _minEntries; i++)
		{
			envelope = envelope.Extend(children[i].Envelope);
			totalMargin += envelope.Margin;
		}

		return totalMargin;
	}
	#endregion

	private int GetBestSplitIndex(List<ISpatialData> children)
	{
		return Enumerable.Range(_minEntries, children.Count - _minEntries)
			.Select(i =>
			{
				var leftEnvelope = GetEnclosingEnvelope(children.Take(i));
				var rightEnvelope = GetEnclosingEnvelope(children.Skip(i));

				var overlap = leftEnvelope.Intersection(rightEnvelope).Area;
				var totalArea = leftEnvelope.Area + rightEnvelope.Area;
				return new { i, overlap, totalArea };
			})
			.OrderBy(x => x.overlap)
			.ThenBy(x => x.totalArea)
			.Select(x => x.i)
			.First();
	}
	#endregion
	#endregion

	#region BuildTree
	private Node BuildTree(ISpatialData[] data)
	{
		var treeHeight = GetDepth(data.Length);
		var rootMaxEntries = (int)Math.Ceiling(data.Length / Math.Pow(this._maxEntries, treeHeight - 1));
		return BuildNodes(new ArraySegment<ISpatialData>(data), treeHeight, rootMaxEntries);
	}

	private int GetDepth(int numNodes) =>
		(int)Math.Ceiling(Math.Log(numNodes) / Math.Log(this._maxEntries));

	private Node BuildNodes(ArraySegment<ISpatialData> data, int height, int maxEntries)
	{
		if (data.Count <= maxEntries)
		{
			return height == 1
				? new Node(data.ToList(), height)
				: new Node(
					new List<ISpatialData>
					{
						BuildNodes(data, height - 1, this._maxEntries),
					},
					height);
		}

		Sort(data, s_compareMinX);

		var nodeSize = (data.Count + (maxEntries - 1)) / maxEntries;
		var subSortLength = nodeSize * (int)Math.Ceiling(Math.Sqrt(maxEntries));

		var children = new List<ISpatialData>(maxEntries);
		foreach (var subData in Chunk(data, subSortLength))
		{
			Sort(subData, s_compareMinY);

			foreach (var nodeData in Chunk(subData, nodeSize))
			{
				children.Add(BuildNodes(nodeData, height - 1, this._maxEntries));
			}
		}

		return new Node(children, height);
	}

	private static IEnumerable<ArraySegment<ISpatialData>> Chunk(ArraySegment<ISpatialData> values, int chunkSize)
	{
		var start = 0;
		while (start < values.Count)
		{
			var len = Math.Min(values.Count - start, chunkSize);
			yield return new ArraySegment<ISpatialData>(values.Array, values.Offset + start, len);
			start += chunkSize;
		}
	}

	private static void Sort(ArraySegment<ISpatialData> data, IComparer<ISpatialData> comparer)
	{
		Array.Sort(data.Array, data.Offset, data.Count, comparer);
	}
	#endregion

	private static Envelope GetEnclosingEnvelope(IEnumerable<ISpatialData> items)
	{
		var envelope = Envelope.EmptyBounds;
		foreach (var data in items)
		{
			envelope = envelope.Extend(data.Envelope);
		}
		return envelope;
	}

	private List<T> GetAllChildren(List<T> list, Node n)
	{
		if (n.IsLeaf)
		{
			list.AddRange(
				n.Items.Cast<T>());
		}
		else
		{
			foreach (var node in n.Items.Cast<Node>())
				GetAllChildren(list, node);
		}

		return list;
	}

}
