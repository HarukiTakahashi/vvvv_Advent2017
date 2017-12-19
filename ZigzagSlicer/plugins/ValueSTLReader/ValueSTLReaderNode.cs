#region usings
using System;
using System.ComponentModel.Composition;
using System.Collections.Generic;
using System.IO;
using System.Text;


using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using VVVV.Utils.VColor;
using VVVV.Utils.VMath;

using VVVV.Core.Logging;
#endregion usings

namespace VVVV.Nodes
{
	#region PluginInfo
	[PluginInfo(Name = "STLReader", Category = "Value", Help = "Basic template with one value in/out", Tags = "")]
	#endregion PluginInfo
	public class ValueSTLReaderNode : IPluginEvaluate
	{
		#region fields & pins
		[Input("Filename", DefaultValue = 1.0)]
		public ISpread<string> Fname;
		
		[Input("Read", DefaultValue = 0)]
		public ISpread<bool> FRead;
		
		[Input("Index", DefaultValue = 1.0)]
		public ISpread<int> FIndex;
		
		[Input("Apply", DefaultValue = 0)]
		public ISpread<bool> FApply;
		
		[Input("Reset", DefaultValue = 0)]
		public ISpread<bool> FReset;
		
		[Output("Vertex")]
		public ISpread<double> FVertex;
		
		[Output("Normal")]
		public ISpread<double> FNormal;
		
		[Output("Indices")]
		public ISpread<double> FIndices;
		
		[Output("bin size")]
		public ISpread<int> FBin;
		
		[Output("Done")]
		public ISpread<bool> FDone;
		
		[Import()]
		public ILogger FLogger;
		#endregion fields & pins
		
		
		Model[] models;
		
		// バイナリファイルの判定
		public bool IsBinaryFile(string filePath)
		{
			FileStream fs = File.OpenRead(filePath);
			int len = (int)fs.Length;
			int count = 0;
			byte[] content = new byte[len];
			int size = fs.Read(content, 0, len);
			
			for (int i = 0; i < size; i++){
				if (content[i] == 0){
					count++;
					if (count == 4){
						return true;
					}
				}
				else{
					count = 0;
				}
			}
			return false;
		}
		
		// STLファイル読み込み
		private void readSTL(int num, string name){
			// ファイル名からオブジェクト生成
			string path = name;
			string file = Path.GetFileName(path);
			
			if(file == ""){
				return;
			}
			//FLogger.Log(LogType.Debug, file + ": Start Loading");
			
			if(IsBinaryFile(path)){
				// バイナリフォーマットの場合
				models[num] = new Model(file);
				using(BinaryReader w = new BinaryReader(File.OpenRead(name))){
					// 任意の文字列
					byte[] ch = w.ReadBytes(80);
					// 三角形の枚数
					int triNum = (int)w.ReadUInt32();
					int count = 0;
					for (int i = 0; i < triNum; i++) {
						models[num].normal.Add(w.ReadSingle());
						models[num].normal.Add(w.ReadSingle());
						models[num].normal.Add(w.ReadSingle());
						for (int j = 0; j < 3; j++) {
							models[num].vertex.Add(w.ReadSingle());
							float tf = w.ReadSingle();
							models[num].vertex.Add(w.ReadSingle());
							models[num].vertex.Add(tf);
							
							models[num].indices.Add(count++);
						}
						
						ch = w.ReadBytes(2);
					}
				}
			}
			else{
				// アスキーフォーマットの場合
				models[num] = new Model(file);
				
				using (StreamReader sr = new StreamReader(name, Encoding.Default)) {
					string line = "";
					int count = 0;
					while((line = sr.ReadLine()) != null){
						
						
						if(line.Contains("normal")){
							string[] sp = line.Split(' ');
							models[num].normal.Add(double.Parse(sp[sp.Length-3]));
							models[num].normal.Add(double.Parse(sp[sp.Length-2]));
							models[num].normal.Add(double.Parse(sp[sp.Length-1]));
						}
						if(line.Contains("vertex")){
							string[] sp = line.Split(' ');
							models[num].vertex.Add(double.Parse(sp[sp.Length-3]));
							models[num].vertex.Add(double.Parse(sp[sp.Length-1]));
							models[num].vertex.Add(double.Parse(sp[sp.Length-2]));
							
							models[num].indices.Add(count++);
						}
					}
					
				}
				//FLogger.Log(LogType.Debug, file + ": Done!");
			}
		}
		
		public void outputSTL(int i){
			int vc = FVertex.SliceCount;
			int nc = FNormal.SliceCount;
			int ic = FIndices.SliceCount;
			
			FVertex.SliceCount += models[i].vertex.Count;
			FNormal.SliceCount += models[i].normal.Count;
			FIndices.SliceCount += models[i].indices.Count;
			
			for(int j = vc; j < FVertex.SliceCount;j++){
				FVertex[j] = models[i].vertex[j-vc];
			}
			for(int j = nc; j < FNormal.SliceCount;j++){
				FNormal[j] = models[i].normal[j-nc];
			}
			for(int j = ic; j < FIndices.SliceCount;j++){
				FIndices[j] = models[i].indices[j-ic];
			}
			
			//FLogger.Log(LogType.Debug, models[i].name + ": Output!");
		}
		
		//called when data for any output pin is requested
		public void Evaluate(int SpreadMax)
		{
			FDone[0] = false;
			if(FReset[0]){
				FVertex.SliceCount = 0;
				FNormal.SliceCount = 0;
				FIndices.SliceCount = 0;
				FDone.SliceCount = 1;
				FBin.SliceCount = 0;
				models = new Model[0];
			}
			
			if(FRead[0] && Fname != null){
				//FLogger.Log(LogType.Debug, "========================");
				models = new Model[Fname.SliceCount];
				
				for (int i = 0; i < Fname.SliceCount; i++){
					readSTL(i,Fname[i]);
				}
			}
			
			if(FApply[0] && models != null){
				//FLogger.Log(LogType.Debug, "========================");
				FVertex.SliceCount = 0;
				FNormal.SliceCount = 0;
				FIndices.SliceCount = 0;
				FBin.SliceCount = Fname.SliceCount;
				for (int i = 0; i < FIndex.SliceCount; i++){
					outputSTL(FIndex[i]%Fname.SliceCount);
					FBin[i] = models[FIndex[i]%Fname.SliceCount].vertex.Count;
				}
				FDone[0] = true;
			}
			//FLogger.Log(LogType.Debug, "hi tty!");
		}
	}
	
	public class Model{
		public List<double> vertex;
		public List<double> normal;
		public List<double> indices;
		public string name;
		
		public Model(string name){
			this.vertex = new List<double>();
			this.normal = new List<double>();
			this.indices = new List<double>();
			this.name = name;
		}
		
	}
}
