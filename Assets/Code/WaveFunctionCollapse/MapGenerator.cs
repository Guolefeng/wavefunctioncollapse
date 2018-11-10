using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System;
using UnityEditor;

public class MapGenerator : MonoBehaviour, IMap, ISerializationCallbackReceiver {
	public const float BlockSize = 2f;

	public static System.Random Random;

	[HideInInspector]
	public Module[] Modules;

	public Dictionary<Vector3i, Slot> Map;

	public int DefaultSize = 4;

	public int Height = 8;

	public bool RespectNeighorExclusions = true;

	public Vector3i RangeLimitCenter;

	public int RangeLimit = 20;

	private DefaultColumn defaultColumn;

	public BoundaryConstraint[] BoundaryConstraints;

	private HashSet<Slot> workArea;

	private Queue<Slot> failureQueue;

	private Queue<Slot> buildQueue;

	public bool Initialized {
		get {
			return this.Map != null;
		}
	}

	public Slot GetSlot(Vector3i position, bool create) {
		if (position.Y >= this.Height || position.Y < 0) {
			return null;
		}

		if (this.Map.ContainsKey(position)) {
			return this.Map[position];
		}
		if (!create) {
			return null;
		}

		if ((position - this.RangeLimitCenter).Magnitude > this.RangeLimit) {
#if UNITY_EDITOR
			Debug.LogWarning("Touched Range Limit!");
#endif
			return null;
		}

		this.Map[position] = new Slot(position, this, this.defaultColumn.GetSlot(position)); ;
		return this.Map[position];
	}

	public Slot GetSlot(Vector3i position) {
		return this.GetSlot(position, true);
	}

	public Slot GetSlot(int x, int y, int z, bool create = true) {
		return this.GetSlot(new Vector3i(x, y, z), create);
	}
	
	public void CreateModules() {
		this.Modules = ModulePrototype.CreateModules(this.RespectNeighorExclusions).ToArray();
	}

	public void Initialize() {
		this.Clear();
		MapGenerator.Random = new System.Random();
		this.Map = new Dictionary<Vector3i, Slot>();
		this.failureQueue = new Queue<Slot>();
		this.buildQueue = new Queue<Slot>();

		if (this.Modules == null || this.Modules.Length == 0) {
			Debug.LogWarning("Module data was not available, creating new data.");
			this.CreateModules();
		}
		this.InitialModuleHealth = this.createInitialModuleHealth(this.Modules);
		this.defaultColumn = new DefaultColumn(this);
	}

	public void Collapse(Vector3i start, Vector3i size, bool showProgress = false) {
		var targets = new List<Vector3i>();
		for (int x = 0; x < size.X; x++) {
			for (int y = 0; y < size.Y; y++) {
				for (int z = 0; z < size.Z; z++) {
					targets.Add(start + new Vector3i(x, y, z));
				}
			}
		}
		this.Collapse(targets, showProgress);
	}

	public void CollapseDefaultArea(bool showProgress = false) {
		this.Collapse(new Vector3i(- this.DefaultSize / 2, 0, - this.DefaultSize / 2), new Vector3i(this.DefaultSize, this.Height, this.DefaultSize), showProgress);
	}

	public void Collapse(IEnumerable<Vector3i> targets, bool showProgress = false) {
		this.workArea = new HashSet<Slot>(targets.Select(target => this.GetSlot(target)).Where(slot => slot != null && !slot.Collapsed));
		
		while (this.workArea.Any()) {
			int minEntropy = this.workArea.Min(slot => slot.Entropy);
			var candidates = this.workArea.Where(slot => !slot.Collapsed && slot.Entropy == minEntropy).ToList();
			
			var selected = candidates[MapGenerator.Random.Next(0, candidates.Count - 1)];
			selected.CollapseRandom();

			if (showProgress) {
				EditorUtility.DisplayProgressBar("Collapsing area... ", this.workArea.Count + " left...", 1f - (float)this.workArea.Count() / targets.Count());
			}
		}

		var retry = new List<Slot>();
		int failureCount = this.failureQueue.Count();
		while (this.failureQueue.Any()) {
			var failedSlot = this.failureQueue.Dequeue();
			if (!failedSlot.TryToRecoverFailure()) {
				retry.Add(failedSlot);
			}
			if (showProgress) {
				EditorUtility.DisplayProgressBar("Handling failed blocks...", this.failureQueue.Count + " left...", 1f - (float)this.failureQueue.Count() / failureCount);
			}
		}
		foreach (var item in retry) {
			this.failureQueue.Enqueue(item);
		}
		if (showProgress) {
			EditorUtility.ClearProgressBar();
		}
	}

	public Vector3 GetWorldspacePosition(Vector3i position) {
		return this.transform.position
			+ Vector3.up * MapGenerator.BlockSize / 2f
			+ new Vector3(
				(float)(position.X) * MapGenerator.BlockSize,
				(float)(position.Y) * MapGenerator.BlockSize,
				(float)(position.Z) * MapGenerator.BlockSize);
	}

	public void Clear() {
		var children = new List<Transform>();
		foreach (Transform child in this.transform) {
			children.Add(child);
		}
		foreach (var child in children) {
			GameObject.DestroyImmediate(child.gameObject);
		}
		this.Map = null;
	}

	public void EnforceWalkway(Vector3i start, int direction) {
		var slot = this.GetSlot(start);
		var toRemove = slot.Modules.Where(module => !module.GetFace(direction).Walkable).ToList();
		slot.RemoveModules(toRemove);
	}

	public void EnforceWalkway(Vector3i start, Vector3i destination) {
		int direction = Orientations.GetIndex((destination - start).ToVector3());
		this.EnforceWalkway(start, direction);
		this.EnforceWalkway(destination, (direction + 3) % 6);
	}

	public void MarkSlotComplete(Slot slot) {
		if (this.workArea != null) {
			this.workArea.Remove(slot);
		}
	}

	public void MarkSlotForBuilding(Slot slot) {
		this.buildQueue.Enqueue(slot);
	}

	public void OnFail(Slot slot) {
		this.failureQueue.Enqueue(slot);
	}

	public void Update() {
		if (this.buildQueue == null) {
			return;
		}

		int maxSpawnsPerFrame = 50;

		while (this.buildQueue.Count != 0 && maxSpawnsPerFrame-- > 0) {
			this.buildQueue.Dequeue().Build();
		}
	}

	public void BuildAllSlots() {
		while (this.buildQueue.Count != 0) {
			this.buildQueue.Dequeue().Build();
		}
	}


	public void OnBeforeSerialize() { }

	public void OnAfterDeserialize() {
		if (this.Modules != null && this.Modules.Length != 0) {
			foreach (var module in this.Modules) {
				module.DeserializeNeigbors(this.Modules);
			}
		}
	}
	
	public bool VisualizeSlots = false;

#if UNITY_EDITOR
	[DrawGizmo(GizmoType.InSelectionHierarchy | GizmoType.NotInSelectionHierarchy)]
	static void DrawGizmoForMyScript(MapGenerator mapGenerator, GizmoType gizmoType) {
		if (!mapGenerator.VisualizeSlots) {
			return;
		}
		if (mapGenerator.Map == null) {
			return;
		}
		foreach (var slot in mapGenerator.Map.Values) {
			if (slot.Collapsed || slot.Modules.Count() == mapGenerator.Modules.Count()) {
				continue;
			}
			Handles.Label(mapGenerator.GetWorldspacePosition(slot.Position), slot.Modules.Count().ToString());
		}
	}
#endif
}
