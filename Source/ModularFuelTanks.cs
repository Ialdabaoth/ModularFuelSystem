//#define DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using KSP;

namespace RealFuels
{
	public class ModuleFuelTanks : ModularFuelPartModule
	{
		public static float massMult = 1.0f;
		public static ConfigNode MFSSettings = null;
		private static bool initialized = false;
		public static Dictionary<string, ConfigNode> stageDefinitions;	// configuration for all parts of this type

		// A FuelTank is a single TANK {} entry from the part.cfg file.
		// it defines four properties:
		// name = the name of the resource that can be stored
		// utilization = how much of the tank is devoted to that resource (vs. how much is wasted in cryogenics or pumps)
		// mass = how much the part's mass is increased per volume unit of tank installed for this resource type
		// loss_rate = how quickly this resource type bleeds out of the tank

		public class FuelTank: IConfigNode
		{
			//------------------- fields
			public string name = "UnknownFuel";
			public string note = "";
			public float utilization = 1.0f;
			public float mass = 0.0f;
			public double loss_rate = 0.0;
			public float temperature = 300.0f;
			public bool fillable = true;

			[System.NonSerialized]
			public ModuleFuelTanks module;

			//------------------- virtual properties
			public int id
			{
				get {
					if(name == null)
						return 0;
					return name.GetHashCode ();
				}
			}

			public Part part
			{
				get {
					if(module == null)
						return null;
					return module.part;
				}
			}

			public PartResource resource
			{
				get {
					if (part == null)
						return null;
					return part.Resources[name];
				}
			}

			public double amount {
				get {
					if (resource == null)
						return 0.0;
					else
						return resource.amount;
				}
				set {
					double newAmount = value;
					if(newAmount > maxAmount)
						newAmount = maxAmount;

					if(resource != null && newAmount >= 0)
						resource.amount = newAmount;
				}
			}

			public double maxAmount {
				get {
					if (resource == null)
						return 0.0f;
					return resource.maxAmount;
				}

				set {
					double newMaxAmount = value;
					if (resource != null && newMaxAmount <= 0.0) {
						amount = 0.0;
						resource.amount = 0.0;
						resource.maxAmount = 0.0;
						PartResource res = resource;
						part.Resources.list.Remove(res);
						PartResource[] allR = part.GetComponents<PartResource>();
						foreach (PartResource r in allR)
							if (r.resourceName.Equals(name))
								DestroyImmediate(r);
						part.Resources.UpdateList();
                        module.ResourcesModified(part);
					} else if (resource != null) {
						double maxQty = module.availableVolume * utilization + maxAmount;
						if (maxQty < newMaxAmount)
							newMaxAmount = maxQty;

						resource.maxAmount = newMaxAmount;
						if(amount > newMaxAmount)
							amount = newMaxAmount;
                        module.ResourcesModified(part);
					} else if(newMaxAmount > 0.0) {
						ConfigNode node = new ConfigNode("RESOURCE");
						node.AddValue ("name", name);
						node.AddValue ("amount", newMaxAmount);
						node.AddValue ("maxAmount", newMaxAmount);
#if DEBUG
						print (node.ToString ());
#endif
						part.AddResource (node);
						resource.enabled = true;
                        module.ResourcesModified(part);
					}
					// update mass here because C# is annoying.
					if (module.basemass >= 0) {
                        float oldMass = part.mass;
						module.basemass = module.basemassPV * (float)module.volume;
						part.mass = module.basemass * massMult + module.tank_mass; // NK for realistic mass
                        module.MassModified(part, oldMass);
                    }
				}
			}

			//------------------- implicit type conversions
			public static implicit operator bool(FuelTank f)
			{
				return (f != null);
			}

			public static implicit operator string(FuelTank f)
			{
				return f.name;
			}

			public override string ToString ()
			{
				if (name == null)
					return "NULL";
				return name;
			}

			//------------------- IConfigNode implementation
			public void Load(ConfigNode node)
			{
				if (node.name.Equals ("TANK") && node.HasValue ("name")) {
					name = node.GetValue ("name");
					if(node.HasValue ("note"))
						note = node.GetValue ("note");
					if (node.HasValue("fillable"))
						bool.TryParse(node.GetValue("fillable"), out fillable);
					if(node.HasValue ("utilization"))
						float.TryParse (node.GetValue("utilization"), out utilization);
					else if(node.HasValue ("efficiency"))
						float.TryParse (node.GetValue("efficiency"), out utilization);
					if(node.HasValue ("temperature"))
						float.TryParse (node.GetValue("temperature"), out temperature);
					if(node.HasValue ("loss_rate"))
						double.TryParse (node.GetValue("loss_rate"), out loss_rate);
					if(node.HasValue ("mass"))
						float.TryParse (node.GetValue("mass"), out mass);
					if(node.HasValue ("maxAmount")) {
						double v;
						if(node.GetValue ("maxAmount").Contains ("%")) {
							double.TryParse(node.GetValue("maxAmount").Replace("%", "").Trim(), out v);
							maxAmount = v * utilization * module.volume * 0.01; // NK
						} else {
							double.TryParse(node.GetValue ("maxAmount"), out v);
							maxAmount = v;
						}
						if(node.HasValue ("amount")) {
                            string amt = node.GetValue("amount").Trim().ToLower();
							if(amt.Equals("full"))
								amount = maxAmount;
                            else if (amt.Contains("%"))
                            {
                                double.TryParse(amt.Replace("%", "").Trim(), out v);
                                amount = v * maxAmount * 0.01;
                            }
                            else
                            {
                                double.TryParse(node.GetValue("amount"), out v);
                                amount = v;
                            }
						} else {
							amount = 0;
						}
					} else {
						maxAmount = 0;
						amount = 0;
					}
				}
			}

			public void Save(ConfigNode node)
			{
				if (name != null) {
					node.AddValue ("name", name);
					node.AddValue ("utilization", utilization);
					node.AddValue ("mass", mass);
					node.AddValue ("temperature", temperature);
					node.AddValue ("loss_rate", loss_rate);
					node.AddValue ("fillable", fillable);
					node.AddValue ("note", note);

					// You would think we want to do this only in the editor, but
					// as it turns out, KSP is terrible about consistently setting
					// up resources between the editor and the launchpad.
					node.AddValue ("amount", amount.ToString("G17"));
					node.AddValue ("maxAmount", maxAmount.ToString("G17"));
				}
			}

			//------------------- Constructor
			public FuelTank()
			{
			}
		}

		public static string GetSetting(string setting, string dflt)
		{
			if (MFSSettings == null) {
				foreach (ConfigNode n in GameDatabase.Instance.GetConfigNodes("MFSSETTINGS"))
					MFSSettings = n;
			}
			if (MFSSettings != null && MFSSettings.HasValue(setting)) {
				return MFSSettings.GetValue(setting);
			}
			return dflt;
		}

		private void InitMFS()
		{
			bool usereal = false;
			bool.TryParse(GetSetting("useRealisticMass", "false"), out usereal);
			if (!usereal)
				massMult = float.Parse(GetSetting("tankMassMultiplier", "1.0"));
			else
				massMult = 1.0f;

			initialized = true;

			stageDefinitions = new Dictionary<string, ConfigNode>();
		}


		//------------- this is all my non-KSP stuff

		public double usedVolume {
			get {
				double v = 0;
				foreach (FuelTank fuel in fuelList) {
					if(fuel.maxAmount > 0 && fuel.utilization > 0)
						v += fuel.maxAmount / fuel.utilization;
				}
				return v;
			}
		}
        
		public double availableVolume {
			get {
				return volume - usedVolume;
			}
		}
		public float tank_massPV = 0.0f;

		public float tank_mass {
			get {
				float m = 0.0f;
				foreach (FuelTank fuel in fuelList) {
#if DEBUG2
					print(String.Format("{0} {1} {2} {3}", fuel.maxAmount, fuel.utilization, fuel.mass, massMult));
#endif
					if(fuel.maxAmount > 0 && fuel.utilization > 0)
						m += (float) fuel.maxAmount * fuel.mass / fuel.utilization * massMult; // NK for realistic masses
				}
				tank_massPV = m / (float)volume;
				return m;
			}
		}

		//------------------- this is all KSP stuff

		[KSPField(isPersistant = true)]
		public float basemass = 0.0f;

		[KSPField(isPersistant = true)]
		public float basemassPV = 0.0f;

        [KSPField(isPersistant = true)]
        public bool dedicated = false;

        // no double support for KSPFields - [KSPField(isPersistant = true)]
		public double volume = 0.0f;

		public ConfigNode stage;		// configuration for this part (instance)
		public List<FuelTank> fuelList;
        // for EngineIgnitor integration: store a public dictionary of all pressurized propellants
        public Dictionary<string, bool> pressurizedFuels;

		public static bool ResourceExists (string name)
		{
			return PartResourceLibrary.Instance.GetDefinition (name) != null;
		}

		public static ConfigNode CheckTankResources (ConfigNode tankdef)
		{
			foreach (var tank in tankdef.GetNodes ("TANK")) {
				if (!ResourceExists (tank.GetValue ("name"))) {
					tankdef.nodes.Remove (tank);
				}
			}
			return tankdef;
		}

		public static ConfigNode TankDefinition(string name)
		{
			foreach (ConfigNode tank in GameDatabase.Instance.GetConfigNodes ("TANK_DEFINITION")) {
				if(tank.HasValue ("name") && tank.GetValue ("name").Equals (name))
					return CheckTankResources (tank);
			}
			return null;
		}

		private void CopyConfigValue(ConfigNode src, ConfigNode dst, string key)
		{
			if (src.HasValue(key)) {
				if(dst.HasValue(key))
					dst.SetValue(key, src.GetValue(key));
				else
					dst.AddValue(key, src.GetValue(key));
			}
		}

		public override void OnInitialize()
		{
#if DEBUG
			print("========ModuleFuelTanks.OnInitialize=======" + (part.vessel != null ? " for " + part.vessel.name : ""));
#endif
			if (fuelList == null || fuelList.Count == 0) {
				fuelList = new List<FuelTank>();

				if (stage == null) {	// OnLoad does not get called in the VAB or SPH
#if DEBUG
					print("copying from stageDefinitions");
#endif
					string part_name = part.name;
					if (part_name.Contains("_"))
						part_name = part_name.Remove(part_name.LastIndexOf("_"));
					if (part_name.Contains("(Clone)"))
						part_name = part_name.Remove(part_name.LastIndexOf("(Clone)"));

					stage = new ConfigNode();
					stageDefinitions[part.name].CopyTo(stage);
				}
				foreach (ConfigNode tankNode in stage.GetNodes("TANK")) {
#if DEBUG
					print("loading FuelTank from node " + tankNode.ToString());
#endif
					FuelTank tank = new FuelTank();
					tank.module = this;
					tank.Load(tankNode);
					fuelList.Add(tank);
				}
                // for EngineIgnitor integration: store a public dictionary of all pressurized propellants
                pressurizedFuels = new Dictionary<string, bool>();
                foreach(FuelTank f in fuelList)
                    pressurizedFuels[f.name] = stage.name.Equals("ServiceModule") || f.note.ToLower().Contains("pressurized");
#if DEBUG
				print("ModuleFuelTanks.onLoad loaded " + fuelList.Count + " fuels");
#endif
			}
		}

        public void ClearFuels()
        {
            if (fuelList == null)
                return;
            foreach (FuelTank t in fuelList)
            {
                t.amount = 0;
                t.maxAmount = 0;
            }
        }

        public void SwitchTankType(string newtype)
        {
            ConfigNode node = new ConfigNode("MODULE");
            node.SetValue("name", "ModuleFuelTanks");
            node.SetValue("type", newtype);
            node.SetValue("volume", volume.ToString("G17"));
            ClearFuels();
            fuelList = null;
            OnLoad(node);
        }

		public override void OnLoad(ConfigNode node)
		{
#if DEBUG
			print ("========ModuleFuelTanks.OnLoad called. Node is:=======");
			print (part.name);
#endif
			if (!initialized)
				InitMFS ();

            // no KSPField support for doubles
            if (node.HasValue("volume"))
                double.TryParse(node.GetValue("volume"), out volume);

			string part_name = part.name;
			if (part_name.Contains("_"))
				part_name = part_name.Remove(part_name.LastIndexOf("_"));
            if (part_name.Contains("(Clone)"))
                part_name = part_name.Remove(part_name.LastIndexOf("(Clone)"));

			stage = new ConfigNode ();

			bool needInitialize = false;
			// Only the part config nodes "type", so missing "type" implies a persistence file or saved craft
			// "volume" is required for part config nodes, but optional for the others
			if (node.HasValue ("type") && node.HasValue ("volume")) {
				string tank_type = node.GetValue ("type");
				ConfigNode tankDef = TankDefinition (tank_type);
				if (tankDef != null)
					tankDef.CopyTo (stage);
				CopyConfigValue (node, stage, "volume");
				CopyConfigValue (node, stage, "basemass");

				stageDefinitions[part_name] = stage;
				needInitialize = true;
			} else {
				stageDefinitions[part_name].CopyTo (stage);
			}
#if DEBUG
			print (stage);
#endif
			// Override tank definitions
			foreach (var tank in node.GetNodes("TANK")) {
				string tank_name = tank.GetValue("name");
				// don't allow tanks for resources that don't exist, unless this is from a saved game.
				if (needInitialize && !ResourceExists (tank_name)) {
					print (String.Format("dropping {0}", tank_name));
					continue;
				}
				ConfigNode stageTank = stage.GetNodes("TANK").FirstOrDefault(p => p.GetValue("name") == tank_name);
				if (stageTank == null) {
					stageTank = stage.AddNode("TANK");
				}
				CopyConfigValue(tank, stageTank, "name");
				CopyConfigValue(tank, stageTank, "fillable");
				CopyConfigValue(tank, stageTank, "utilization");
				CopyConfigValue(tank, stageTank, "mass");
				CopyConfigValue(tank, stageTank, "temperature");
				CopyConfigValue(tank, stageTank, "loss_rate");
				CopyConfigValue(tank, stageTank, "amount");
				CopyConfigValue(tank, stageTank, "maxAmount");
				CopyConfigValue(tank, stageTank, "note");
			}

			// NK use custom basemass
			if (stage.HasValue("basemass")) {
				string base_mass = stage.GetValue("basemass");
#if DEBUG
				print (String.Format("basemass: {0} {1}", basemass, base_mass));
#endif
				if (base_mass.Contains("*") && base_mass.Contains("volume")) {
					float.TryParse(base_mass.Replace("volume", "").Replace("*", "").Trim(), out basemass);
					basemassPV = basemass;
					basemass = basemass * (float)volume;
				} else {
					// NK allow static basemass
					float.TryParse(base_mass.Trim(), out basemass);
					basemassPV = basemass / (float)volume;
				}
			}

			if (needInitialize) {
				OnInitialize ();
				UpdateMass ();
			}
#if DEBUG
			print ("ModuleFuelTanks loaded. ");
#endif
		}

		public override void OnSave (ConfigNode node)
		{
            base.OnSave(node);

            node.AddValue("volume", volume.ToString("G17")); // no KSPField support for doubles

#if DEBUG
			print ("========ModuleFuelTanks.OnSave called. Node is:=======");
			print (node.ToString ());
#endif
			if (fuelList == null)
				fuelList = new List<FuelTank> ();
			foreach (FuelTank tank in fuelList) {
				ConfigNode subNode = new ConfigNode("TANK");
				tank.Save (subNode);
#if DEBUG
				print ("========ModuleFuelTanks.OnSave adding subNode:========");
				print (subNode.ToString());
#endif
				node.AddNode (subNode);
				tank.module = this;
			}
		}

		private void UsePrefab()
		{
			Part prefab = null;
			prefab = part.symmetryCounterparts.Find(pf => pf.Modules.Contains("ModuleFuelTanks")
														  && ((ModuleFuelTanks)pf.Modules["ModuleFuelTanks"]).fuelList != null
														  && ((ModuleFuelTanks)pf.Modules["ModuleFuelTanks"]).fuelList.Count > 0);
#if DEBUG
			print ("ModuleFuelTanks.OnStart: copying from a symmetryCounterpart with a ModuleFuelTanks PartModule");
#endif
			ModuleFuelTanks pModule = (ModuleFuelTanks) prefab.Modules["ModuleFuelTanks"];
			if(pModule == this) {
				print ("ModuleFuelTanks.OnStart: Copying from myself won't do any good.");
			} else {
				ConfigNode node = new ConfigNode("MODULE");
				pModule.OnSave (node);
#if DEBUG
				print ("ModuleFuelTanks.OnStart node from prefab:" + node);
#endif
				OnLoad (node);
			}
		}

		
        private void ResourcesModified (Part part)
		{
            UpdateTweakableMenu();
			BaseEventData data = new BaseEventData (BaseEventData.Sender.USER);
			data.Set<Part> ("part", part);
			part.SendEvent ("OnResourcesModified", data, 0);
		}

		private void MassModified (Part part, float oldmass)
		{
			BaseEventData data = new BaseEventData (BaseEventData.Sender.USER);
			data.Set<Part> ("part", part);
			data.Set<float> ("oldmass", oldmass);
			part.SendEvent ("OnMassModified", data, 0);
		}

		public override void OnStart (StartState state)
		{
            base.OnStart(state);
#if DEBUG
			print ("========ModuleFuelTanks.OnStart( State == " + state.ToString () + ")=======");
#endif

			if (basemass == 0 && part != null)
				basemass = part.mass;
			if(fuelList == null || fuelList.Count == 0) {
				// In the editor, OnInitialize doesn't get called for the root part (KSP bug?)
				// First check if it's a counterpart.
				if(HighLogic.LoadedSceneIsEditor
				   && part.symmetryCounterparts.Count > 0) {
					UsePrefab();
				} else {
					if(fuelList != null)
						foreach (FuelTank tank in fuelList)
							tank.module = this;

					OnInitialize();
				}
			}
			UpdateMass();

			ResourcesModified (part);

            if (state == StartState.Editor)
            {
				UpdateSymmetryCounterparts();
                UpdateTweakableMenu();
					// if we detach and then re-attach a configured tank with symmetry on, make sure the copies are configured.
            }
		}

		public void CheckSymmetry()
		{
#if DEBUG
			print ("ModuleFuelTanks.CheckSymmetry for " + part.partInfo.name);
#endif
			EditorLogic editor = EditorLogic.fetch;
			if (editor != null && editor.editorScreen == EditorLogic.EditorScreen.Parts && part.symmetryCounterparts.Count > 0) {
#if DEBUG
				print ("ModuleFuelTanks.CheckSymmetry: updating " + part.symmetryCounterparts.Count + " other parts.");
#endif
				UpdateSymmetryCounterparts();
			}
#if DEBUG
			print ("ModuleFuelTanks checked symmetry");
#endif
		}

		public override void OnUpdate ()
		{
			if (HighLogic.LoadedSceneIsEditor) {
				return;
			}
			if (timestamp > 0)
                CalculateTankLossFunction(precisionDeltaTime);

            base.OnUpdate();            //Needs to be at the end to prevent weird things from happening during startup and to make handling persistance easy; this does mean that the effects are delayed by a frame, but since they're constant, that shouldn't matter here
        }

        private void CalculateTankLossFunction(double deltaTime)
        {
            foreach (FuelTank tank in fuelList)
            {
                if (tank.loss_rate > 0 && tank.amount > 0)
                {
                    double deltaTemp = part.temperature - tank.temperature;
                    if (deltaTemp > 0)
                    {
                        double loss = tank.maxAmount * tank.loss_rate * deltaTemp * deltaTime; // loss_rate is calibrated to 300 degrees.
                        if (loss > tank.amount)
                            tank.amount = 0;
                        else
                            tank.amount -= loss;
                    }
                }
            }

        }

		public override string GetInfo ()
		{
			string info = "Modular Fuel Tank: \n"
				+ "  Max Volume: " + volume.ToString () + "\n"
					+ "  Tank can hold:";
			foreach(FuelTank tank in fuelList) {
				info += "\n   " + tank + " " + tank.note;
			}
			return info + "\n";
		}

		// looks to see if we should ignore this fuel when creating an autofill for an engine
		public static bool IgnoreFuel(string name)
		{
			ConfigNode fNode = MFSSettings.GetNode("IgnoreFuelsForFill");
			if (fNode != null) {
				foreach (ConfigNode.Value v in fNode.values)
					if (v.name.Equals(name))
						return true;
			}
			return false;
		}

        private List<string> mixtures = new List<string>();

        public static string myToolTip = "";
		int counterTT = 0;
		public void OnGUI()
		{
			EditorLogic editor = EditorLogic.fetch;
            if (!HighLogic.LoadedSceneIsEditor || !editor) {
                return;
            }
            bool dirty = false;
            List<string> new_mixtures = new List<string>();
            foreach (Part engine in GetEnginesFedBy(part))
            {
                FuelInfo fi = new FuelInfo(engine, this);
                if (fi.ratio_factor > 0 && !new_mixtures.Contains(fi.label))
                {
                    new_mixtures.Add(fi.label);
                    if (!mixtures.Contains(fi.label))
                        dirty = true;
                }
            }
            foreach (string label in mixtures)
            {
                if (!new_mixtures.Contains(label))
                    dirty = true;
            }
            if (dirty)
            {
                UpdateTweakableMenu();
                mixtures = new_mixtures;
            }

            if(editor.editorScreen != EditorLogic.EditorScreen.Actions)
				return;

			if (EditorActionGroups.Instance.GetSelectedParts ().Contains (part)) {
				//Rect screenRect = new Rect(0, 365, 430, (Screen.height - 365));
				Rect screenRect = new Rect(0, 365, 438, (Screen.height - 365));
				//Color reset = GUI.backgroundColor;
				//GUI.backgroundColor = Color.clear;
				GUILayout.Window (part.name.GetHashCode (), screenRect, fuelManagerGUI, "Fuel Tanks for " + part.partInfo.title);
				//GUI.backgroundColor = reset;

				//if(!(myToolTip.Equals("")))
				GUI.Label(new Rect(440, Screen.height - Input.mousePosition.y, 300, 20), myToolTip);
			}
		}

		static GUIStyle unchanged = null;
		static GUIStyle changed = null;
		static GUIStyle greyed = null;
		static GUIStyle overfull = null;

		Vector2 scrollPos;
		private List<string> textFields;
		
        public class FuelInfo
		{
			public string names;
			public List<Propellant> propellants;
			public double efficiency;
			public double ratio_factor;

            public string label
            {
                get
                {
                    string label = "";
                    foreach (Propellant tfuel in propellants)
                    {
                        if (PartResourceLibrary.Instance.GetDefinition(tfuel.name).resourceTransferMode != ResourceTransferMode.NONE)
                        {
                            if (label.Length > 0)
                                label += " / ";
                            label += Math.Round(100 * tfuel.ratio / ratio_factor, 0).ToString() + "% " + tfuel.name;
                        }
                    }
                    return label;
                }
            }

            public FuelInfo(Part engine, ModuleFuelTanks tank)
            {
                // tank math:
                // efficiency = sum[utilization * ratio]
                // then final volume per fuel = fuel_ratio / fuel_utilization / efficiency

                ratio_factor = 0.0;
                efficiency = 0.0;

                propellants = new List<Propellant>();
                if (engine.Modules.Contains("ModuleEnginesFX"))
                {
                    ModuleEnginesFX e = (ModuleEnginesFX)engine.Modules["ModuleEnginesFX"];
                    foreach (Propellant p in e.propellants)
                        propellants.Add(p);
                }
                else if (engine.Modules.Contains("ModuleEngines"))
                {
                    ModuleEngines e = (ModuleEngines)engine.Modules["ModuleEngines"];
                    foreach (Propellant p in e.propellants)
                        propellants.Add(p);
                }

                foreach (Propellant tfuel in propellants)
                {
                    if (PartResourceLibrary.Instance.GetDefinition(tfuel.name) == null)
                    {
                        print("Unknown RESOURCE {" + tfuel.name + "}");
                        ratio_factor = 0.0;
                        break;
                    }
                    else if (PartResourceLibrary.Instance.GetDefinition(tfuel.name).resourceTransferMode == ResourceTransferMode.NONE)
                    {
                        //ignore this propellant, since it isn't serviced by fuel tanks
                    }
                    else
                    {
                        FuelTank t = tank.fuelList.Find(f => f.ToString().Equals(tfuel.name));
                        if (t)
                        {
                            efficiency += tfuel.ratio / t.utilization;
                            ratio_factor += tfuel.ratio;
                        }
                        else if (!IgnoreFuel(tfuel.name))
                        {
                            ratio_factor = 0.0;
                            break;
                        }
                    }
                }
                this.names = "Used by: " + engine.partInfo.title;
            }
		}

        UIPartActionWindow _myWindow = null;
        UIPartActionWindow myWindow
        {
            get
            {
                if (_myWindow == null)
                {
                    foreach (UIPartActionWindow window in FindObjectsOfType(typeof(UIPartActionWindow)))
                    {
                        if (window.part == part) _myWindow = window;
                    }
                }
                return _myWindow;
            }
        }

        [KSPField(isPersistant = false, guiActive = false, guiActiveEditor = true, guiName = "Available Volume: ", guiUnits = "L", guiFormat = "F4")]
        public double available_volume;


        public void UpdateTweakableMenu()
        {
            if (HighLogic.LoadedSceneIsEditor && !dedicated)
            {
                available_volume = availableVolume;
                Fields["available_volume"].guiActiveEditor = true;
                
                Events["Empty"].guiActiveEditor = (usedVolume != 0);
                List<BaseEvent> removeList = new List<BaseEvent>();
                foreach (BaseEvent button in Events)
                {
                    if (button.name.Contains("MFT"))
                        removeList.Add(button);
                }
                foreach (BaseEvent button in removeList)
                    Events.Remove(button);
                

                if (availableVolume >= 0.001)
                {
                    List<string> labels = new List<string>();
                    foreach(Part engine in GetEnginesFedBy(part))
                    {
                        FuelInfo f = new FuelInfo(engine, this);
                        int i = 0;
                        if (f.ratio_factor > 0.0)
                        {
                            if (!labels.Contains(f.label))
                            {
                                labels.Add(f.label);
                                KSPEvent kspEvent = new KSPEvent();
                                kspEvent.name = "MFT" + (++i).ToString();
                                kspEvent.guiActive = false;
                                kspEvent.guiActiveEditor = true;
                                kspEvent.guiName = f.label;
                                BaseEvent button = new BaseEvent(Events, kspEvent.name, () =>
                                {
                                    ConfigureFor(engine);
                                }, kspEvent);
                                button.guiActiveEditor = true;
                                Events.Add(button);
                            }
                        }
                    }
                }
                if (myWindow != null)
                    myWindow.displayDirty = true;

            }
        }
		public void fuelManagerGUI(int WindowID)
		{
			if (unchanged == null) {
                if(GUI.skin == null)
                {
                    unchanged = new GUIStyle();
                    changed = new GUIStyle();
                    greyed = new GUIStyle();
                    overfull = new GUIStyle();
                }
                else
                {
                    unchanged = new GUIStyle(GUI.skin.textField);
                    changed = new GUIStyle(GUI.skin.textField);
                    greyed = new GUIStyle(GUI.skin.textField);
                    overfull = new GUIStyle(GUI.skin.label);
                }

				unchanged.normal.textColor = Color.white;
				unchanged.active.textColor = Color.white;
				unchanged.focused.textColor = Color.white;
				unchanged.hover.textColor = Color.white;

				changed.normal.textColor = Color.yellow;
				changed.active.textColor = Color.yellow;
				changed.focused.textColor = Color.yellow;
				changed.hover.textColor = Color.yellow;

				greyed.normal.textColor = Color.gray;

				overfull.normal.textColor = Color.red;
			}

			GUILayout.BeginVertical ();

			GUILayout.BeginHorizontal();
			GUILayout.Label ("Current mass: " + Math.Round(part.mass + part.GetResourceMass(),4) + " Ton(s)");
			GUILayout.Label("Dry mass: " + Math.Round(part.mass,4) + " Ton(s)");
			GUILayout.EndHorizontal ();

			if (fuelList.Count == 0) {

                foreach (Part sPart in part.symmetryCounterparts)
                {
                    if (sPart.Modules.Contains("ModuleFuelTanks"))
                    {
                        ModuleFuelTanks fuel = (ModuleFuelTanks)sPart.Modules["ModuleFuelTanks"];
                        if (fuel && fuel.fuelList != null && fuel.fuelList.Count > 0)
                        {
                            fuelList = fuel.fuelList;
                            break;
                        }
                    }
                }
				if (fuelList.Count == 0) {
                    GUILayout.BeginHorizontal();
				    GUILayout.Label ("This fuel tank cannot hold resources.");
				    GUILayout.EndHorizontal ();
				    return;
                }
			}

			GUILayout.BeginHorizontal();
			if (Math.Round(availableVolume, 4) < 0) {
                GUILayout.Label("Available volume: " + availableVolume.ToString("N3") + " / " + volume.ToString("N3"), overfull);
			} else {
                GUILayout.Label("Available volume: " + availableVolume.ToString("N3") + " / " + volume.ToString("N3"));
			}
			GUILayout.EndHorizontal ();

			scrollPos = GUILayout.BeginScrollView(scrollPos);

			int text_field = 0;
			if (textFields == null)
				textFields = new List<string> ();

			foreach (ModuleFuelTanks.FuelTank tank in fuelList) {
				GUILayout.BeginHorizontal();
				/*
                int amountField = text_field;
				text_field++;
				if(textFields.Count < text_field) {
					textFields.Add (tank.amount.ToString());
				}*/
				int maxAmountField = text_field;
				text_field++;
				if(textFields.Count < text_field) {
					textFields.Add (tank.maxAmount.ToString());
				}
				GUILayout.Label(" " + tank, GUILayout.Width (120));
				if(part.Resources.Contains(tank) && part.Resources[tank].maxAmount > 0) {
					GUIStyle style;
                    /*
					if(tank.fillable) {
						style = unchanged;
						if (textFields[amountField] != tank.amount.ToString()) {
							style = changed;
						}
						textFields[amountField] = GUILayout.TextField(textFields[amountField], style, GUILayout.Width (65));
					} else {
						GUILayout.Label ("None", greyed, GUILayout.Width (65));
					}
					GUILayout.Label("/", GUILayout.Width (5));
                    */
					style = unchanged;
					if (textFields[maxAmountField] != tank.maxAmount.ToString()) {
						style = changed;
					}
					textFields[maxAmountField] = GUILayout.TextField(textFields[maxAmountField], style, GUILayout.Width (140));

					GUILayout.Label(" ", GUILayout.Width (5));

					if(GUILayout.Button ("Update", GUILayout.Width (60))) {
						double newMaxAmount = tank.maxAmount;
						double newAmount = tank.amount;

						if (textFields[maxAmountField].Trim() == "") {
							newMaxAmount = 0;
						} else {
							double tmp;
							if(double.TryParse (textFields[maxAmountField], out tmp))
								newMaxAmount = tmp;
						}

						if(!tank.fillable) {// || textFields[amountField].Trim() == "") {
							newAmount = 0;
							//print("empty amount");
						} else {
							//double tmp;
							//if(double.TryParse(textFields[amountField], out tmp))
                            newAmount = newMaxAmount;//tmp;
							//print("amount " + textFields[amountField] + " " + newAmount.ToString());
						}

						tank.maxAmount = newMaxAmount;
						tank.amount = newAmount;

						//textFields[amountField] = tank.amount.ToString();
						textFields[maxAmountField] = tank.maxAmount.ToString();

						ResourcesModified (part);
						if(part.symmetryCounterparts.Count > 0)
							UpdateSymmetryCounterparts();
					}
					if(GUILayout.Button ("Remove", GUILayout.Width (60))) {
						tank.maxAmount = 0;
						//textFields[amountField] = "0";
						textFields[maxAmountField] = "0";
						ResourcesModified (part);
						if(part.symmetryCounterparts.Count > 0)
							UpdateSymmetryCounterparts();
					}
				} else if(availableVolume >= 0.001) {
                    string extraData = "Max: " + Math.Round(availableVolume * tank.utilization, 2).ToString("N2") + " (+" + Math.Round(availableVolume * tank.mass, 4).ToString("N4") + " tons)";

					GUILayout.Label(extraData, GUILayout.Width (150));

					if(GUILayout.Button("Add", GUILayout.Width (130))) {
						tank.maxAmount = availableVolume * tank.utilization;
						if(tank.fillable)
							tank.amount = tank.maxAmount;
						else
							tank.amount = 0;

						//textFields[amountField] = tank.amount.ToString();
						textFields[maxAmountField] = tank.maxAmount.ToString();

						ResourcesModified (part);
						if(part.symmetryCounterparts.Count > 0)
							UpdateSymmetryCounterparts();

					}
				} else {
					GUILayout.Label ("  No room for tank.", GUILayout.Width (150));

				}
				GUILayout.EndHorizontal ();

			}

			GUILayout.BeginHorizontal();
			if(GUILayout.Button ("Remove All Tanks")) {
                Empty();
            }
			GUILayout.EndHorizontal();
            List<Part> enginesList = GetEnginesFedBy(part);

            if (enginesList.Count > 0 && availableVolume >= 0.001)
            {
				Dictionary<string, FuelInfo> usedBy = new Dictionary<string, FuelInfo>();

				GUILayout.BeginHorizontal();
				GUILayout.Label ("Configure remaining volume for " + enginesList.Count + " engines:");
				GUILayout.EndHorizontal();

                foreach (Part engine in enginesList)
                {
                    FuelInfo f = new FuelInfo(engine, this);
                    if (f.ratio_factor > 0.0) {
						if (!usedBy.ContainsKey(f.label)) {
							usedBy.Add(f.label, f);
						} else {
							if (!usedBy[f.label].names.Contains(engine.partInfo.title)) {
                                usedBy[f.label].names += ", " + engine.partInfo.title;
							}
						}
					}
				}
				if (usedBy.Count > 0) {
					foreach (string label in usedBy.Keys) {
						GUILayout.BeginHorizontal();
						if (GUILayout.Button(new GUIContent(label, usedBy[label].names))) {
							textFields.Clear();
                            ConfigureFor(usedBy[label]);
						}
						GUILayout.EndHorizontal();
					}
				}
			}
			GUILayout.EndScrollView();
			GUILayout.EndVertical ();
			if(!(myToolTip.Equals("")) && GUI.tooltip.Equals("")) {
				if(counterTT > 4) {
					myToolTip = GUI.tooltip;
					counterTT = 0;
				} else {
					counterTT++;
				}
			} else {
				myToolTip = GUI.tooltip;
				counterTT = 0;
			}
			//print("GT: " + GUI.tooltip);
		}

        [KSPEvent(guiName = "Remove All Tanks", guiActive = false, guiActiveEditor = true, name="Empty")]
        public void Empty()
        {
            foreach (ModuleFuelTanks.FuelTank tank in fuelList)
            {
                tank.amount = 0;
                tank.maxAmount = 0;
            }
            ResourcesModified(part);
            if (part.symmetryCounterparts.Count > 0)
                UpdateSymmetryCounterparts();
            if (textFields != null)
                textFields.Clear();
            Events["Empty"].guiActiveEditor = false;
        }



        public void ConfigureFor(Part engine)
        {
            ConfigureFor(new FuelInfo(engine, this));
        }

        public void ConfigureFor(FuelInfo fi)
        {
            if (fi.ratio_factor == 0.0 || fi.efficiency == 0) // can't configure for this engine
                return;

            double total_volume = availableVolume;
            foreach (Propellant tfuel in fi.propellants)
            {
                if (PartResourceLibrary.Instance.GetDefinition(tfuel.name).resourceTransferMode != ResourceTransferMode.NONE)
                {
                    ModuleFuelTanks.FuelTank tank = fuelList.Find(t => t.name.Equals(tfuel.name));
                    if (tank)
                    {
                        double amt = total_volume * tfuel.ratio / fi.efficiency;
                        tank.maxAmount += amt;
                        tank.amount += amt;
                    }
                }
            }
            ResourcesModified(part);
            if (part.symmetryCounterparts.Count > 0)
                UpdateSymmetryCounterparts();
            if (textFields != null)
                textFields.Clear();
        }

		public static List<Part> GetEnginesFedBy(Part part)
		{
			Part ppart = part;
			while (ppart.parent != null && ppart.parent != ppart)
				ppart = ppart.parent;

            return new List<Part>(ppart.FindChildParts<Part>(true)).FindAll(p => (p.Modules.Contains("ModuleEngines") || p.Modules.Contains("ModuleEnginesFX")));
		}

        //called by StretchyTanks
        public void ChangeVolume(double newVolume)
        {
            //print("*MFS* Setting new volume " + newVolume);

            double oldUsedVolume = volume;
            if (availableVolume > 0.0001)
                oldUsedVolume = volume - availableVolume;

            double volRatio = newVolume / volume;
            double availVol = availableVolume * volRatio;
            if (availVol < 0.0001)
                availVol = 0;
            double newUsedVolume = newVolume - availVol;

            if(volume < newVolume)
                volume = newVolume; // do it now only if we're resizing up, else we'll fail to resize tanks.

            double ratio = newUsedVolume / oldUsedVolume;
            for (int i = 0; i < fuelList.Count; i++)
            {
                ModuleFuelTanks.FuelTank tank = fuelList[i];
                double oldMax = tank.maxAmount;
                double oldAmt = tank.amount;
                tank.maxAmount = oldMax * ratio;
                tank.amount = tank.maxAmount * ratio;
            }

            volume = newVolume; // update volume after tank resizes to avoid case where tank resizing clips new volume

            if (textFields != null)
                textFields.Clear();
            UpdateMass();
        }

		public void UpdateMass()
		{
#if DEBUG
			print ("=== MFS: UpdateMass: " + basemass.ToString() + " , " + basemassPV.ToString() + " , " + volume.ToString() + " , " + massMult.ToString() + " , " + tank_mass.ToString());
#endif
			float oldmass = part.mass;
			if (basemass >= 0) {
				basemass = basemassPV * (float)volume;
				part.mass = basemass * massMult + tank_mass; // NK for realistic mass
			}
			MassModified (part, oldmass);
		}

		public int UpdateSymmetryCounterparts()
		{
			int i = 0;

			if (part.symmetryCounterparts == null)
				return i;
			foreach(Part sPart in part.symmetryCounterparts) {
				if (sPart.Modules.Contains("ModuleFuelTanks")) {
					ModuleFuelTanks fuel = (ModuleFuelTanks)sPart.Modules["ModuleFuelTanks"];
					if (fuel) {
						i++;
						if (fuel.fuelList == null)
							continue;
						foreach (ModuleFuelTanks.FuelTank tank in fuel.fuelList) {
							tank.amount = 0;
							tank.maxAmount = 0;
						}
						foreach (ModuleFuelTanks.FuelTank tank in this.fuelList) {
							if (tank.maxAmount > 0) {
								ModuleFuelTanks.FuelTank pTank = fuel.fuelList.Find(t => t.name.Equals(tank.name));
								if (pTank) {
									pTank.maxAmount = tank.maxAmount;
									if(tank.maxAmount > 0)
										pTank.amount = tank.amount;
								}
							}
						}
						ResourcesModified (fuel.part);
						fuel.UpdateMass();
					}
				}
			}
			return i;
		}
	}
}
