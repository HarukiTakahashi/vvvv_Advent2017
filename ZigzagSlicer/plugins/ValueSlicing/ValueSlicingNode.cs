#region usings
using System;
using System.ComponentModel.Composition;
using System.Collections.Generic;

using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using VVVV.Utils.VColor;
using VVVV.Utils.VMath;

using VVVV.Core.Logging;
#endregion usings

namespace VVVV.Nodes
{
	#region PluginInfo
	
	[PluginInfo(Name = "Slicing", Category = "Value", Help = "Basic template with one value in/out", Tags = "")]
	#endregion PluginInfo
	public class ValueSlicingNode : IPluginEvaluate
	{
		#region fields & pins
		[Input("ObjectVertices", DefaultValue = 1.0)]
		public ISpread<Vector3D> FVertex;
		
		[Input("Indices", DefaultValue = 1.0)]
		public ISpread<int> FIndex;
		
		[Input("IndexCount", DefaultValue = 0.0)]
		public ISpread<int> FIndexCount;
		
		[Input("Normals", DefaultValue = 1.0)]
		public ISpread<Vector3D> FNorm;
		
		[Input("BaseY - LayerHeight - RoadWidth", DefaultValue = 0.0)]
		public ISpread<double> FParam;
		
		[Input("ModelSize", DefaultValue = 0.0)]
		public ISpread<Vector3D> FModelParam;
		
		[Input("Start", DefaultValue = 0.0)]
		public ISpread<bool> FStart;
		
		[Input("Reset", DefaultValue = 0.0)]
		public ISpread<bool> FReset;
		
		[Input("DebugHeight", DefaultValue = 0.0)]
		public ISpread<int> FD;
		
		// ==========================
		
		[Output("Message")]
		public ISpread<string> FMes;
		
		[Output("innerSupportPoints")]
		public ISpread<Vector3D> FOutiSP;
		[Output("innerModelPoints")]
		public ISpread<Vector3D> FOutiMP;
		
		[Output("CrossedPoints")]
		public ISpread<Vector3D> FOutCP;
		
		[Output("innerSupportInOut")]
		public ISpread<bool> FOutiSPInOut;
		
		[Output("Gcode")]
		public ISpread<string> FGcode;
		
		[Output("doWrite")]
		public ISpread<bool> FDoWrite;
		
		[Output("doneInit")]
		public ISpread<bool> FDoneInit;
		
		[Output("AllPoints")]
		public ISpread<Vector3D> FOutAllPoints;
		
		[Output("PointsBin")]
		public ISpread<int> FOutPointsBin;
		
		[Output("AllSupportPoints")]
		public ISpread<Vector3D> FOutSupAllPoints;
		
		[Output("SupportPointsBin")]
		public ISpread<int> FOutSupPointsBin;
		
		
		[Import()]
		public ILogger FLogger;
		#endregion fields & pins
		
		double FILAMENT_DIAMETER = 1.75;
		double NOZZLE_WIDTH = 0.4;
		
		// 使いやすように名前つけとく
		double BASE_HEIGHT;		// 初期の高さ
		double LAYER_HEIGHT;	// 層ごとのオフセット
		double THETA;
		double R;
		double SPEED_MOVE;
		double SPEED_NORMAL_EXTRUDE;
		double SPEED_ZIGZAG_EXTRUDE;
		double SPEED_RETRACTION;
		double RETRACT_AMOUNT;
		double MODEL_HEIGHT;
		double FILAMENT_AREA;
		double INTERNAL_WIDTH;
		Vector3D MODEL_SIZE_MIN;
		Vector3D MODEL_SIZE_MAX;
		
		double SUPPORT_LAYER_COUNT; // サポート構造の層の数
		double SUPPORT_STARTLAYER_COUNT; // サポート構造の最初の層の数
		double Start_Height;
		
		// ループの管理用
		int Slice_Num;
		int Current_Slice;
		
		double OVER_HANG;
		
		
		Vector3D[] points;
		List<Face> faces;
		List<string> Gcode;
		List<Face> partialFaces;
		
		// 複数箇所にわかれている場合の対策
		List<List<Vector3D>> oneOuterRoad;
		
		List<List<Vector3D>>[] outerPath;
		List<List<Vector3D>>[] supportPath;
		
		List<PointWithLabel>[] innerPathX;
		List<List<PointWithLabel>> innerSupportPathX;
		List<List<PointWithLabel>> innerSupportPathZ;
		
		bool createFlag = false;
		bool initFlag = false;
		
		// 初期化処理 ==================================
		private void init(){
			// パラメータの整理
			BASE_HEIGHT = FParam[0];
			LAYER_HEIGHT = FParam[1];
			THETA = FParam[2];
			R = FParam[3];
			SPEED_MOVE = FParam[4];
			SPEED_NORMAL_EXTRUDE = FParam[5];
			SPEED_ZIGZAG_EXTRUDE = FParam[6];
			SPEED_RETRACTION = 3600;
			RETRACT_AMOUNT = FParam[7];
			OVER_HANG = FParam[8];
			MODEL_HEIGHT = FModelParam[1].y;
			INTERNAL_WIDTH = FParam[9];
			
			MODEL_SIZE_MIN = new Vector3D(FModelParam[0]);
			MODEL_SIZE_MAX = new Vector3D(FModelParam[1]);
			
			
			createFlag = false;
			
			// 開始位置の決定
			Start_Height = R * Math.Cos(radians(THETA));
			// スライス枚数
			Slice_Num = (int)((MODEL_HEIGHT - BASE_HEIGHT) / LAYER_HEIGHT) + 1;
			// 全部の外周を保存する
			outerPath = new List<List<Vector3D>>[Slice_Num];
			// サポート用のリストもここで作っておく
			supportPath = new List<List<Vector3D>>[Slice_Num];
			
			Current_Slice = 0;
			
			// サポートの積層ピッチを決める
			for(int i = 1; i < 100; i++){
				double t = LAYER_HEIGHT / i;
				if(t < 0.35){
					SUPPORT_LAYER_COUNT = i;
					break;
				}
			}
			for(int i = 1; i < 100; i++){
				double t = Start_Height / i;
				if(t < 0.35){
					SUPPORT_STARTLAYER_COUNT = i;
					break;
				}
			}
			
			Gcode = new List<string>();
			partialFaces = new List<Face>();
			
			FILAMENT_AREA = (FILAMENT_DIAMETER/2)*(FILAMENT_DIAMETER/2)*Math.PI;
			
			int indCount = FIndexCount[0];
			points = new Vector3D[indCount];
			bool[] pointsLookUp = new bool[indCount];
			faces = new List<Face>();
			
			// ポイントに順番に入れていく
			// 面も作る
			for(int i = 0; i < indCount; i+=3){
				int t1 = FIndex[i], t2 = FIndex[i+1], t3 = FIndex[i+2];
				
				if(	pointsLookUp[t1] == false){
					pointsLookUp[t1] = true;
					points[t1] = FVertex[t1];
				}
				if(	pointsLookUp[t2] == false){
					pointsLookUp[t2] = true;
					points[t2] = FVertex[t2];
				}
				if(	pointsLookUp[t3] == false){
					pointsLookUp[t3] = true;
					points[t3] = FVertex[t3];
				}
				
				faces.Add(new Face(points[t1],points[t2],points[t3]));
			}
			
			// 面の連結
			// 指数時間なのが気になるけど，事前処理だからまぁいいか
			for(int i = 0; i < faces.Count; i++){
				Face f = faces[i];
				for(int j = 0; j < faces.Count; j++){
					if(f.checkNeighborsFace()){ break; }
					if(i == j){ continue; }
					
					f.connectNeighbor(faces[j]);
				}
			}
			
			Gcode = new List<string>();
			// 初期設定
			Gcode.Add("; Output");
			Gcode.Add("M83");
			Gcode.Add("G92 E0");
			Gcode.Add("G1 Z0\n");
			
			FGcode.SliceCount = Gcode.Count;
			FGcode.AssignFrom(Gcode);
			FMes.SliceCount = 1;
			FDoneInit.SliceCount = 1;
			FDoneInit[0] =true;
			FDoWrite.SliceCount = 1;
			FDoWrite[0] = true;
		}
		
		// 角度→ラジアンの変換
		private double radians(double angle)
		{
			return angle/180*Math.PI;
		}
		
		// 交点を返す
		private Vector3D getCross(Vector3D p1, Vector3D p2, double y){
			double hi = (y - p1.y) / (p2.y - p1.y);
			Vector3D tv = new Vector3D(p1.x + hi*(p2.x - p1.x),y, p1.z + hi*(p2.z - p1.z));
			return tv;
		}
		
		// 交差判定
		private bool isCrossed(Vector3D p1, Vector3D p2, double y){
			if(p1.y > y && p2.y < y || p2.y > y && p1.y < y){
				return true;
			}
			return false;
		}
		
		// 外周を作る =============================================================
		#region 外部構造の生成
		
		// 再帰呼び出し ====================================
		// 一個前のFaceも一緒に入れると良さそう？
		private void connecting(Face f, Vector3D pv, double h){
			if(f == null){
				// １周したらnullになるので
				return;
			}
			if(f.mark == -1 || f.mark == 1){
				// チェック対象外 or チェック済みならさようなら
				// -1になることはないはずだけど
				return;
			}
			
			Vector3D nextV = pv; // 仮に割当
			Face nextF = null;
			
			if(isCrossed(f.p1,f.p2,h)){
				// 辺12
				if(f.adjF12.mark == 1){
					// 辺12に隣接しているやつがチェック済み（1つ前）なら
					// それを参照して中点は追加しない
					f.cp12 = pv;
				}
				else{
					f.cp12 = getCross(f.p1,f.p2,h);
					oneOuterRoad[oneOuterRoad.Count-1].Add(f.cp12);
					nextV = f.cp12;
					nextF = f.adjF12;
				}
			}
			if(isCrossed(f.p2,f.p3,h)){
				// 辺23
				if(f.adjF23.mark == 1){
					f.cp23 = pv;
				}
				else{
					f.cp23 = getCross(f.p2,f.p3,h);
					oneOuterRoad[oneOuterRoad.Count-1].Add(f.cp23);
					nextV = f.cp23;
					nextF = f.adjF23;
				}
			}
			if(isCrossed(f.p1,f.p3,h)){
				// 辺31
				if(f.adjF31.mark == 1){
					f.cp31 = pv;
				}
				else{
					f.cp31 = getCross(f.p3,f.p1,h);
					oneOuterRoad[oneOuterRoad.Count-1].Add(f.cp31);
					nextV = f.cp31;
					nextF = f.adjF31;
				}
			}
			
			// チェック済みとする
			f.mark = 1;
			connecting(nextF,nextV,h);
		}
		
		// 外周を作る =============================================================
		private void createOuter(double h){
			
			int ze = -1000;
			
			// 平面と交差する面を拾っていく
			partialFaces = new List<Face>();
			
			foreach(Face f in faces){
				f.cp12.x = ze; f.cp12.y = ze; f.cp12.z = ze;
				f.cp23.x = ze; f.cp23.y = ze; f.cp23.z = ze;
				f.cp31.x = ze; f.cp31.y = ze; f.cp31.z = ze;
				
				if(f.isCross(h)){
					f.mark = 0; // 交差する（未チェック状態）
					partialFaces.Add(f);
				}else{
					f.mark = -1; // 交差しない
				}
			}
			
			// その高さのパスを格納
			oneOuterRoad = new List<List<Vector3D>>();
			
			// 中点の計算と連結
			for(int i = 0; i < partialFaces.Count; i++){
				if(partialFaces[i].mark == 1){
					// チェック済みなら次の面へ
					continue;
				}else{
					// ここで一周が作られるはずなので，新しいリストを生成する
					// とりあえず島が一つの場合を想定
					oneOuterRoad.Add(new List<Vector3D>());
					
					Face f = partialFaces[i];
					// 1枚目の面を処理してしまう
					// これで 12 → 23 または 12 → 31 の順で入る
					if(isCrossed(f.p1,f.p2,h)){
						f.cp12 = getCross(f.p1,f.p2,h);
						oneOuterRoad[oneOuterRoad.Count-1].Add(f.cp12);
					}
					if(isCrossed(f.p2,f.p3,h)){
						f.cp23 = getCross(f.p2,f.p3,h);
						oneOuterRoad[oneOuterRoad.Count-1].Add(f.cp23);
					}
					if(isCrossed(f.p1,f.p3,h)){
						f.cp31 = getCross(f.p3,f.p1,h);
						oneOuterRoad[oneOuterRoad.Count-1].Add(f.cp31);
					}
					
					// チェック済みにする
					f.mark = 1;
					
					// 再帰呼び出し
					if(f.cp31.x != ze && f.cp31.y != ze && f.cp31.z != ze){
						// cp31 が決まっていればここから
						connecting(f.adjF31, f.cp31, h);
					}else{
						// 決まっていない場合は，cp23から
						connecting(f.adjF23, f.cp23, h);
					}
				}
			}
			
			// 外壁の点を綺麗に
			for(int i = 0; i < oneOuterRoad.Count; i++){
				if(oneOuterRoad[i].Count == 0){ continue; }
				for(int j = 0; j < oneOuterRoad[i].Count; j++){
					for(int k = 0; k < oneOuterRoad[i].Count; k++){
						if(k == j){continue;}
						if((oneOuterRoad[i][k] - oneOuterRoad[i][j]).Length < 0.1 ){
							oneOuterRoad[i].RemoveAt(k--);
							//FLogger.Log(LogType.Debug, "Remove!");
						}
					}
				}
			}
		}
		#endregion
		
		// 内部構造のためのメソッド =========================================================
		// 交差判定用
		// 判定対象は，xかz軸に平行な直線
		private bool isCrossForInner(Vector3D v1, Vector3D v2, double l, string axis){
			if(axis == "x"){
				if(v1.x <= l && v2.x > l || v2.x < l && v1.x >= l){
					return true;
				}
				else{
					return false;
				}
			}else{
				if(v1.z <= l && v2.z > l || v2.z < l && v1.z >= l){
					return true;
				}
				else{
					return false;
				}
			}
		}
		
		// 交点を返す
		private Vector3D CrossedPointForInner(Vector3D v1, Vector3D v2, double Y, double l, string axis){
			if(axis == "x"){
				
				double t = Math.Abs((l - v1.x) / (v2.x - v1.x));
				Vector3D rv = new Vector3D(l,Y,v1.z + t * (v2.z - v1.z));
				return rv;
			}else{
				double t = (l - v1.z) / (v2.z - v1.z);
				Vector3D rv = new Vector3D(v1.x + t * (v2.x - v1.x),Y,l);
				return rv;
			}
			
		}
		
		// 距離計算
		private double distance(Vector3D v1,Vector3D v2){
			return Math.Sqrt((v2.x-v1.x)*(v2.x-v1.x)+(v2.y-v1.y)*(v2.y-v1.y)+(v2.z-v1.z)*(v2.z-v1.z));
		}
		
		// ソート
		private void innerSort(List<PointWithLabel> vl, Vector3D s, string axis){
			if(axis == "x"){
				// とりあえず最小値選択で
				for(int i = 0; i < vl.Count; i++){
					double min = Math.Abs(s.z - vl[i].v.z);
					int minIndex = i;
					for(int j = i; j < vl.Count; j++){
						double tdist = Math.Abs(s.z - vl[j].v.z);
						if(tdist < min){
							min = tdist;
							minIndex = j;
						}
					}
					// 交換する
					PointWithLabel tpwl = vl[i];
					vl[i] = vl[minIndex];
					vl[minIndex] = tpwl;
				}
			}
			else{
				// とりあえず最小値選択で
				for(int i = 0; i < vl.Count; i++){
					double min = Math.Abs(s.x - vl[i].v.x);
					int minIndex = i;
					for(int j = i; j < vl.Count; j++){
						double tdist = Math.Abs(s.x - vl[j].v.x);
						if(tdist < min){
							min = tdist;
							minIndex = j;
						}
					}
					// 交換する
					PointWithLabel tpwl = vl[i];
					vl[i] = vl[minIndex];
					vl[minIndex] = tpwl;
				}
			}
			
		}
		
		// 連続を取り除く
		private void removeContinuity(List<PointWithLabel> tl){
			for(int i = 0; i < tl.Count-1;i++){
				if(tl[i].label && tl[i+1].label || !tl[i].label && !tl[i+1].label){
					tl.RemoveAt(i+1);
					i--;
				}
			}
			for(int i = tl.Count-1; i > 0;i--){
				if(tl[i].label && tl[i+1].label || !tl[i].label && !tl[i+1].label){
					tl.RemoveAt(i-1);
				}
			}
		}
		
		// 指定された頂点がモデルの輪郭の中に含まれているか
		private bool isVectorInnerContuor(List<List<Vector3D>> vl, Vector3D cp){
			int cn = 0;
			
			for(int i = 0; i < vl.Count; i++){
				cn = 0;
				for(int j = 0; j < vl[i].Count; j++){
					// この2つが作る辺を調べる
					Vector3D up1 = vl[i][j];
					Vector3D up2 = vl[i][(j+1)%vl[i].Count];
					
					// 上向きの辺。点Pがz軸方向について、始点と終点の間にある。ただし、終点は含まない。(ルール1)
					// 下向きの辺。点Pがz軸方向について、始点と終点の間にある。ただし、始点は含まない。(ルール2)
					if( ((up1.z <= cp.z) && (up2.z > cp.z))|| ((up1.z > cp.z) && (up2.z <= cp.z)) ){
						
						// ルール1,ルール2を確認することで、ルール3も確認できている。
						// 辺は点pよりも右側にある。ただし、重ならない。(ルール4)
						// 辺が点pと同じ高さになる位置を特定し、その時のxの値と点pのxの値を比較する。
						double vt = (cp.z - up1.z) / (up2.z - up1.z);
						if(cp.x <= (up1.x + (vt * (up2.x - up1.x)))){
							++cn;
						}
					}
				}
				if(cn % 2 == 1){
					// 奇数だったら含まれている
					return true;
				}
			}
			return false;
		}
		
		// 線分同士の交点を返す
		private Vector3D CrossedLinesPoint(Vector3D p1, Vector3D p2,Vector3D p3,Vector3D p4) {
			double dev = (p2.z-p1.z)*(p4.x-p3.x)-(p2.x-p1.x)*(p4.z-p3.z);
			double d1 = (p3.z*p4.x-p3.x*p4.z);
			double d2 = (p1.z*p2.x-p1.x*p2.z);
			
			double apx = (d1*(p2.x-p1.x) - d2*(p4.x-p3.x)) / dev;
			double apz = (d1*(p2.z-p1.z) - d2*(p4.z-p3.z)) / dev;
			
			return new Vector3D(apx,p1.y,apz);
			
		}
		
		// 線分同士の交差判定
		private bool isCrossedLines(Vector3D p1, Vector3D p2,Vector3D p3,Vector3D p4) {
			double ta = (p3.x - p4.x) * (p1.z - p3.z) + (p3.z - p4.z) * (p3.x - p1.x);
			double tb = (p3.x - p4.x) * (p2.z - p3.z) + (p3.z - p4.z) * (p3.x - p2.x);
			double tc = (p1.x - p2.x) * (p3.z - p1.z) + (p1.z - p2.z) * (p1.x - p3.x);
			double td = (p1.x - p2.x) * (p4.z - p1.z) + (p1.z - p2.z) * (p1.x - p4.x);
			if(tc * td <= 0 && ta * tb <= 0){
				return true;
			}
			return false;
		}
		
		// 線分と点の距離
		private double distPointAndLine(Vector3D A,Vector3D B, Vector3D P){
			if ( dot(B-A, P-A) < 0 ) return (P-A).Length;
			if ( dot(A-B, P-B) < 0 ) return (P-B).Length;
			return (((B-A).CrossRH(P-A)) / (B-A).Length).Length;
		}
		
		
		// 内部構造のためのメソッド =========================================================
		
		// 内部構造とサポートを作る =========================================================
		#region 内部構造の生成
		private void createInner(int sn){
			// 幅
			double isw = 2; // サポートの内部構造の間隔
			double imw = INTERNAL_WIDTH+0.1; // モデルの内部構造の間隔
			
			double Width_Threshold = INTERNAL_WIDTH+0.1;
			double ModeltoSupport = INTERNAL_WIDTH-0.1; // サポートとモデル間の距離
			int margin = 2;
			bool oflag = false;
			bool sflag = false;
			
			
			int numMX = (int)(Math.Abs(MODEL_SIZE_MIN.x - MODEL_SIZE_MAX.x) / imw)+2;
			int numMZ = (int)(Math.Abs(MODEL_SIZE_MIN.z - MODEL_SIZE_MAX.z) / imw)+2;
			
			// FLogger.Log(LogType.Debug, " in == 内部構造の本数 X方向: " + numX + " Z方向: " + numZ);
			
			// X 方向
			// １層分のサポート構造を管理するリスト
			innerSupportPathX = new List<List<PointWithLabel>>();
			
			// Z 方向
			// １層分の内部構造を管理するリスト
			// innerPathZ = new List<Vector3D>[numMZ];
			// １層分のサポート構造を管理するリスト
			innerSupportPathZ = new List<List<PointWithLabel>>();
			
			// 1層分だけ取り出して使用する
			if(outerPath[sn] != null){
				FLogger.Log(LogType.Debug, " in == モデルの内部構造を作ります");
				oflag = true;
			}
			if(supportPath[sn] != null ){
				FLogger.Log(LogType.Debug, " in == サポートを作ります");
				sflag = true;
			}
			
			FOutCP.SliceCount = 0;
			
			double Y = BASE_HEIGHT + LAYER_HEIGHT * sn;
			FLogger.Log(LogType.Debug, " in == Yの高さ : " + Y);
			
			#region support loop
			// サポートを処理
			if(sflag){
				List<List<Vector3D>> o = outerPath[sn];
				List<List<Vector3D>> support = supportPath[sn];
				
				List<Vector3D> gridX = new List<Vector3D>();
				List<Vector3D> gridZ = new List<Vector3D>();
				
				// X 方向
				for(double zgrid = MODEL_SIZE_MIN.z-margin; zgrid <  MODEL_SIZE_MAX.z+margin; zgrid+=isw){
					for(double xgrid = MODEL_SIZE_MIN.x-margin; xgrid <  MODEL_SIZE_MAX.x+margin; xgrid+=isw){
						Vector3D gridpoint = new Vector3D(xgrid,Y,zgrid);
						if(!isVectorInnerContuor(o,gridpoint)){
							if(isVectorInnerContuor(support,gridpoint)){
								gridX.Add(gridpoint);
							}
						}
					}
				}
				
				// Z 方向
				for(double xgrid = MODEL_SIZE_MIN.x-margin; xgrid <  MODEL_SIZE_MAX.x+margin; xgrid+=isw){
					for(double zgrid = MODEL_SIZE_MIN.z-margin; zgrid <  MODEL_SIZE_MAX.z+margin; zgrid+=isw){
						Vector3D gridpoint = new Vector3D(xgrid,Y,zgrid);
						if(!isVectorInnerContuor(o,gridpoint)){
							if(isVectorInnerContuor(support,gridpoint)){
								gridZ.Add(gridpoint);
							}
						}
					}
				}
				
				for(int i = 0; i < o.Count; i++){
					for(int j = 0; j < o[i].Count; j++){
						Vector3D p1 = o[i][j];
						Vector3D p2 = o[i][(j+1)%o[i].Count];
						
						for(int g = gridX.Count-1; g >= 0; g--){
							double L = distPointAndLine(p1,p2,gridX[g]);
							if(L < ModeltoSupport){
								gridX.RemoveAt(g);
							}
						}
						for(int g = gridZ.Count-1; g >= 0; g--){
							double L = distPointAndLine(p1,p2,gridZ[g]);
							if(L < ModeltoSupport){
								gridZ.RemoveAt(g);
							}
						}
					}
				}
				
				FOutCP.SliceCount = gridZ.Count;
				FOutCP.AssignFrom(gridZ);
				
				
				// innerSupportPathX のなかにいれる
				int lineNum = 0;
				bool breakflag;
				
				innerSupportPathX.Add(new List<PointWithLabel>());
				
				for(int i = 0; i < gridX.Count-2; i++){
					breakflag = false;
					
					// 開始地点はとりあえず入れる
					innerSupportPathX[lineNum].Add(new PointWithLabel(gridX[i],true));
					
					for(int j = i+1; j < gridX.Count; j++){
						if(breakflag){ break; }
						
						if(gridX[i].z != gridX[j].z){
							// 次の列に行った
							if(gridX[i] == gridX[j-1]){
								innerSupportPathX[lineNum].Clear();
							}
							else if(innerSupportPathX[lineNum][innerSupportPathX[lineNum].Count-1].label){
								innerSupportPathX[lineNum].Add(new PointWithLabel(gridX[j-1],false));
							}else if(j == gridX.Count-1){
								// 最後まで見た
								i = j;
								innerSupportPathX[lineNum].Add(new PointWithLabel(gridX[j],false));
								break;
							}
							
							lineNum++;
							i = j-1;
							innerSupportPathX.Add(new List<PointWithLabel>());
							break;
						}
						
						// 交差するかどうか判定
						for(int oi = 0; oi < o.Count; oi++){
							if(breakflag){ break; }
							
							for(int oj = 0; oj < o[oi].Count; oj++){
								// 外壁の1辺
								Vector3D p3 = o[oi][oj];
								Vector3D p4 = o[oi][(oj+1)%o[oi].Count];
								if(isCrossedLines(gridX[i],gridX[j],p3,p4)){
									// 交差する
									if(gridX[i] != gridX[j-1]){
										// 開始地点ではない場合は終点として追加
										innerSupportPathX[lineNum].Add(new PointWithLabel(gridX[j-1],false));
										i = j-1;
										breakflag = true;
										break;
									}else{
										// 開始地点の場合は開始地点を削除
										innerSupportPathX[lineNum].RemoveAt(innerSupportPathX[lineNum].Count-1);
										i = j-1;
										breakflag = true;
										break;
									}
								}
							}
						}
					}
				}
				
				// innerSupportPathZ のなかにいれる
				lineNum = 0;
				innerSupportPathZ.Add(new List<PointWithLabel>());
				
				for(int i = 0; i < gridZ.Count-2; i++){
					breakflag = false;
					
					// 開始地点はとりあえず入れる
					innerSupportPathZ[lineNum].Add(new PointWithLabel(gridZ[i],true));
					
					for(int j = i+1; j < gridZ.Count; j++){
						if(breakflag){ break; }
						
						if(gridZ[i].x != gridZ[j].x){
							// 次の列に行った
							if(gridZ[i] == gridZ[j-1]){
								innerSupportPathZ[lineNum].Clear();
							}
							else if(innerSupportPathZ[lineNum][innerSupportPathZ[lineNum].Count-1].label){
								innerSupportPathZ[lineNum].Add(new PointWithLabel(gridZ[j-1],false));
							}
							else if(j == gridZ.Count-1){
								// 最後まで見た
								i = j;
								innerSupportPathZ[lineNum].Add(new PointWithLabel(gridZ[j],false));
								break;
							}
							lineNum++;
							i = j-1;
							innerSupportPathZ.Add(new List<PointWithLabel>());
							break;
						}
						
						// 交差するかどうか判定
						for(int oi = 0; oi < o.Count; oi++){
							if(breakflag){ break; }
							for(int oj = 0; oj < o[oi].Count; oj++){
								// 外壁の1辺
								Vector3D p3 = o[oi][oj];
								Vector3D p4 = o[oi][(oj+1)%o[oi].Count];
								if(isCrossedLines(gridZ[i],gridZ[j],p3,p4)){
									// 交差する
									if(gridZ[i] != gridZ[j-1]){
										// 開始地点ではない場合は終点として追加
										innerSupportPathZ[lineNum].Add(new PointWithLabel(gridZ[j-1],false));
										i = j-1;
										breakflag = true;
										break;
									}else{
										// 開始地点の場合は開始地点を削除
										innerSupportPathZ[lineNum].RemoveAt(innerSupportPathZ[lineNum].Count-1);
										i = j-1;
										breakflag = true;
										break;
									}
								}
							}
						}
					}
				}
				
			}
			# endregion
			
			
			#region model loop
			// モデルを処理
			if(oflag && outerPath[sn] != null){
				// まずモデル外周の頂点から一番端の点を見つける
				List<List<Vector3D>> outer = outerPath[sn];
				// 複数に分かれている時用に
				double edgeMin=1000, edgeMax=-1000, pitch = 0;
				int LineNum = 0;
				edgeMin = outer[0][0].x;
				edgeMax = outer[0][0].x;
				
				// 端の頂点を見つける
				for(int i = 0; i < outer.Count; i++){
					
					
					// ここで端の頂点が決まる
					for(int j = 0; j < outer[i].Count; j++){
						if(outer[i][j].x < edgeMin){
							edgeMin = outer[i][j].x;
						}
						if(outer[i][j].x > edgeMax){
							edgeMax = outer[i][j].x;
						}
					}
					
					// 内部構造の幅を決める
					for(int k = 0; k < 100; k++){
						double w = Math.Abs(edgeMax - edgeMin);
						if( w / k < Width_Threshold){
							LineNum = k;
							pitch = w / k;
							break;
						}
					}
				}
				
				// １層分の内部構造を管理するリスト
				innerPathX = new List<PointWithLabel>[LineNum];
				
				// Rayを飛ばす
				for(int l = 0; l < LineNum; l++){
					// そのRayのX座標
					double d = edgeMin + (l * pitch) + pitch/2;
					Vector3D s = new Vector3D(d,Y,MODEL_SIZE_MIN.z - margin);
					innerPathX[l] = new List<PointWithLabel>();
					for(int i = 0; i < outer.Count; i++){
						for(int j = 0; j < outer[i].Count; j++){
							Vector3D p1 = outer[i][j];
							Vector3D p2 = outer[i][(j+1)%outer[i].Count];
							
							
							if(isCrossForInner(p1,p2,d,"x")){
								// 交差する辺である
								
								//FLogger.Log(LogType.Debug, "   交差したよ！！！！  " + l);
								Vector3D tv = CrossedPointForInner(p1,p2,Y,d,"x");
								innerPathX[l].Add(new PointWithLabel(tv,false));
							}
							
						}
					}
					// 1本の線を入れ終えたので
					if(innerPathX[l].Count > 0){
						innerSort(innerPathX[l],s,"x");
						
						// 入出力を決める
						for(int k = 0; k < innerPathX[l].Count; k++){
							if(k % 2 == 0){innerPathX[l][k].label = true;}
							else{innerPathX[l][k].label = false;}
						}
						
						// 位置をずらす
						for(int k = 0; k < innerPathX[l].Count; k++){
							PointWithLabel tp = innerPathX[l][k];
							Vector3D tv = tp.v;
							if(tp.label){
								tv.z += pitch / 2;
								tp.v = tv;
							}else{
								tv.z -= pitch; // 終点は膨らみがちになるので
								tp.v = tv;
							}
						}
						
					}
				}
				
			}
			
			
			# endregion
			
			
			// デバッグ
			if(sflag){
				FOutiSP.SliceCount = 0;
				FOutiSPInOut.SliceCount = 0;
				
				for(int iii = 0; iii < innerSupportPathX.Count; iii++){
					FOutiSP.SliceCount += innerSupportPathX[iii].Count;
					FOutiSPInOut.SliceCount += innerSupportPathX[iii].Count;
					for(int i = 0; i < innerSupportPathX[iii].Count; i++){
						FOutiSP[(FOutiSP.SliceCount) - innerSupportPathX[iii].Count + i] = innerSupportPathX[iii][i].v;
						FOutiSPInOut[(FOutiSP.SliceCount) - innerSupportPathX[iii].Count + i] = innerSupportPathX[iii][i].label;
					}
				}
				
				for(int iii = 0; iii < innerSupportPathZ.Count; iii++){
					FOutiSP.SliceCount += innerSupportPathZ[iii].Count;
					FOutiSPInOut.SliceCount += innerSupportPathZ[iii].Count;
					for(int i = 0; i < innerSupportPathZ[iii].Count; i++){
						FOutiSP[(FOutiSP.SliceCount) - innerSupportPathZ[iii].Count + i] = innerSupportPathZ[iii][i].v;
						FOutiSPInOut[(FOutiSP.SliceCount) - innerSupportPathZ[iii].Count + i] = innerSupportPathZ[iii][i].label;
					}
				}
				
			}
			if(oflag){
				FOutiMP.SliceCount = 0;
				for(int iii = 0; iii < innerPathX.Length; iii++){
					FOutiMP.SliceCount += innerPathX[iii].Count;
					for(int i = 0; i < innerPathX[iii].Count; i++){
						FOutiMP[(FOutiMP.SliceCount) - innerPathX[iii].Count + i] = innerPathX[iii][i].v;
					}
				}
				
			}
			
		}
		#endregion
		
		// サポートのためのメソッド =====================================================================
		// 点a,bを端点とする線分と点cとの距離
		private double distance_ls_p(Vector3D a, Vector3D b, Vector3D c) {
			if ( dot(b-a, c-a) < 0.0 ) return (c-a).Length;
			if ( dot(a-b, c-b) < 0.0 ) return (c-b).Length;
			return ((b-a).CrossRH(c-a).Length / (b-a).Length);
		}
		
		// 点a,bを端点とする線分と点cとの距離が最短になる頂点
		private Vector3D distance_p(Vector3D a, Vector3D b, Vector3D c) {
			if ( dot(b-a, c-a) < 0.0 ) {
				return a;
			}
			if ( dot(a-b, c-b) < 0.0 ) {
				return b;
			}
			
			Vector3D normAB = (b-a)/(b-a).Length;
			double t = dot(normAB, c-a);
			return new Vector3D(a + (t * normAB));
		}
		
		// a1,a2を端点とする線分とb1,b2を端点とする線分の交点計算
		private Vector3D intersection_ls(Vector3D a1, Vector3D a2, Vector3D b1, Vector3D b2) {
			Vector3D b = b2-b1;
			double d1 = (a1-b1).CrossRH(b).Length;
			double d2 = (a2-b1).CrossRH(b).Length;
			double t = d1 / (d1 + d2);
			return a1 + (a2-a1) * t;
		}
		// サポートのためのメソッド =====================================================================
		
		// サポート構造を作る ==================================
		#region サポート構造の生成
		private void createSupport(){
			bool breakflag = false;
			// 一番上の層から見ていく
			// ただし一番上の層は除く
			for(int i = outerPath.Length-1; i >= 0; i--){
				if(i > 0){
					
					// もし今の層が，一つ上の層の頂点をすべて含んでいれば，サポートは不要なのでそれを調べる
					breakflag = false;
					
					// FLogger.Log(LogType.Debug, "===== 処理開始 =====");
					// FLogger.Log(LogType.Debug, "" + i + " 層目 パート: " +  outerPath[i].Count);
					
					// 何箇所に別れるか
					for(int cj = 0; cj < outerPath[i].Count; cj++){
						for(int ck = 0; ck < outerPath[i][cj].Count; ck++){
							breakflag = false;
							// すべての頂点を調べる
							// いま調査する頂点
							Vector3D cp = outerPath[i][cj][ck];
							
							// その頂点の内外判定用
							int cn = 0;
							// 一つ下の層 ======================================================
							for(int uj = 0; uj < outerPath[i-1].Count; uj++){
								for(int uk = 0; uk < outerPath[i-1][uj].Count; uk++){
									// この2つが作る辺を調べる
									Vector3D up1 = outerPath[i-1][uj][uk];
									Vector3D up2 = outerPath[i-1][uj][(uk+1)%outerPath[i-1][uj].Count];
									
									// 上向きの辺。点Pがz軸方向について、始点と終点の間にある。ただし、終点は含まない。(ルール1)
									// 下向きの辺。点Pがz軸方向について、始点と終点の間にある。ただし、始点は含まない。(ルール2)
									if( ((up1.z <= cp.z) && (up2.z > cp.z))|| ((up1.z > cp.z) && (up2.z <= cp.z)) ){
										
										// ルール1,ルール2を確認することで、ルール3も確認できている。
										// 辺は点pよりも右側にある。ただし、重ならない。(ルール4)
										// 辺が点pと同じ高さになる位置を特定し、その時のxの値と点pのxの値を比較する。
										double vt = (cp.z - up1.z) / (up2.z - up1.z);
										if(cp.x < (up1.x + (vt * (up2.x - up1.x)))){
											++cn;
										}
									}
									
									if(cp.x == up1.x && cp.z == up1.z || cp.x == up2.x && cp.z == up2.z){
										// 重なっている場合は含んでいる判定
										breakflag = true;
										//FLogger.Log(LogType.Debug, " 重なったよ ");
									}
									
									double crs = (up2.x - up1.x) * (cp.z - up1.z) - (up2.z - up1.z) * (cp.x - up1.x);
									double tt = (cp.x - up2.x) / (up1.x - up2.x);
									if(crs == 0 && tt > 0 && tt < 1){
										// 外積が0になるので直線上にある
										// 一時形式でttが0以上1以下なら，線分上にある
										breakflag = true;
										//FLogger.Log(LogType.Debug, " 線分上にあるよ ");
									}
									
									double d = LAYER_HEIGHT / Math.Tan(radians(OVER_HANG));
									Vector3D ncp = new Vector3D(cp);
									ncp.y -= LAYER_HEIGHT;
									if(distance_ls_p(up1,up2,ncp) < d){
										// 近いところにある
										breakflag = true;
										//FLogger.Log(LogType.Debug, " 近いところにあるよ 距離：" + distance_ls_p(up1,up2,ncp));
									}
								}
							}
							
							// 一つ下の層 ======================================================
							// FLogger.Log(LogType.Debug, " cn: " + cn + " flag: " + breakflag);
							
							if(cn % 2 == 1){
								// 奇数だったら含まれている
								breakflag =true;
							}
							
							if(!breakflag){
								supportPath[i-1] = new List<List<Vector3D>>(outerPath[i]);
								for(int j = 0; j < supportPath[i-1].Count;j++){
									supportPath[i-1][j] = new List<Vector3D>(outerPath[i][j]);
									for(int k = 0; k < supportPath[i-1][j].Count;k++){
										Vector3D v = supportPath[i-1][j][k];
										v.y -= LAYER_HEIGHT;
										supportPath[i-1][j][k] = v;
									}
								}
								breakflag = true;
								break;
							}
						}
					}
				}
				
				// サポート構造の調整
				// 何箇所に別れているか
				if(supportPath[i] != null){
					// サポートまでの距離のしきい値 ！！！！！！
					double d = LAYER_HEIGHT / Math.Tan(radians(OVER_HANG));
					for(int sj = 0; sj < supportPath[i].Count; sj++){
						
						for(int sk = 0; sk < supportPath[i][sj].Count; sk++){
							// サポート構造のすべての頂点を調べる
							// いま注目している点
							Vector3D cv = supportPath[i][sj][sk];
							
							int min_oj = -1;
							int min_ok = -1;
							
							// いくつの頂点を調整したか
							
							for(int oj = 0; oj < outerPath[i].Count; oj++){
								for(int ok = 0; ok < outerPath[i][oj].Count; ok++){
									// モデルのすべての頂点を調べる
									Vector3D v1 = outerPath[i][oj][ok];
									Vector3D v2 = outerPath[i][oj][(ok+1)%outerPath[i][oj].Count];
									
									// サポートの頂点から，モデルの外周への距離
									if(distance_ls_p(v1,v2,cv) < d){
										min_oj = oj;
										min_ok = ok;
										d = distance_ls_p(v1,v2,cv);
									}
								}
							}
							
							// 位置が変わっていたら，一番近い位置へ移動する
							if(min_oj != -1 && min_ok != -1){
								Vector3D v1 = outerPath[i][min_oj][min_ok];
								Vector3D v2 = outerPath[i][min_oj][(min_ok+1)%outerPath[i][min_oj].Count];
								
								supportPath[i][sj][sk] = new Vector3D(distance_p(v1,v2,cv));
							}
						}
					}
					
				}
				
				// サポート構造の調整
				if(i < outerPath.Length-2){
					// n-1層目のサポート
					if(supportPath[i] == null){
						// もしその層にサポートがなければ新しく作る
						supportPath[i] = new List<List<Vector3D>>();
					}
					if(supportPath[i+1] != null){
						for(int j = 0; j < supportPath[i+1].Count; j++){
							List<Vector3D> tlv = new List<Vector3D>();
							tlv.AddRange(supportPath[i+1][j]);
							for(int k = 0; k < tlv.Count; k++){
								Vector3D tv = tlv[k];
								tv.y -= LAYER_HEIGHT;
								tlv[k] = tv;
							}
							supportPath[i].Add(tlv);
							
							// ここで，
						}
					}
					// この段階で角層のサポートが確定している
					
				}
				
			}
		}
		#endregion
		
		
		// 内積！
		private double dot(Vector3D v1, Vector3D v2){
			return v1.x*v2.x + v1.y*v2.y + v1.z*v2.z;
		}
		
		
		
		// Gcodeの生成のためのメソッド ======================================================
		// 単にstringにするだけ
		private string toGcodeXY(Vector3D v){
			string g = " X" + (v.x).ToString("F4") + " Y" + (v.z).ToString("F4") + " F"+SPEED_MOVE;
			
			return g;
		}
		
		// Eの値も計算 普通の造形
		private string toGcodeCalcE(Vector3D vp, Vector3D vc){
			double len = (vp - vc).Length;
			double e = NOZZLE_WIDTH * (LAYER_HEIGHT / SUPPORT_LAYER_COUNT) / FILAMENT_AREA * len * 0.9;
			
			string g = " X" + (vc.x).ToString("F4") + " Y" + (vc.z).ToString("F4") + " E" + e.ToString("F4") + " F"+SPEED_NORMAL_EXTRUDE;
			if(Double.IsNaN(vp.x) || Double.IsNaN(vp.x) || Double.IsNaN(vc.x) || Double.IsNaN(vc.x)){ g = ""; }
			
			return g;
		}
		
		// Eの値も計算 Zigzagの計算
		private string toGcodeZigzagCalcE(Vector3D vp, Vector3D vc){
			double len = (vp - vc).Length;
			double e = R * (Math.Sin(radians(THETA)) * (NOZZLE_WIDTH / FILAMENT_AREA)) * len;
			
			string g = " X" + (vc.x).ToString("F4") + " Y" + (vc.z).ToString("F4") + " E" + e.ToString("F4") + " F"+SPEED_ZIGZAG_EXTRUDE;
			if(Double.IsNaN(vp.x) || Double.IsNaN(vp.x) || Double.IsNaN(vc.x) || Double.IsNaN(vc.x)){ g = ""; }
			
			return g;
		}
		
		private void supportGcodeSub(){
			// サポート構造を順に出力していく
			// X方向
			List<List<PointWithLabel>> sp = innerSupportPathX;
			if(sp.Count > 0){
				for(int l = 0; l < sp.Count; l++){
					if(sp[l] == null){continue;}
					if(sp[l].Count > 0){
						// まずスタート位置に
						Gcode.Add("G1" + toGcodeXY(sp[l][0].v));
						for(int si = 0; si < sp[l].Count-1; si++){
							if(sp[l][si].label){
								Gcode.Add("G1" + " E" + (RETRACT_AMOUNT) + " F" + SPEED_MOVE+ " ; Re-retraction!");
								Gcode.Add("G1" + toGcodeCalcE(sp[l][si].v, sp[l][si+1].v));
								Gcode.Add("G1" + " E" + (RETRACT_AMOUNT*-1) + " F" + SPEED_MOVE+ " ; Re-retraction!");
							}else{
								Gcode.Add("G1" + toGcodeXY(sp[l][si+1].v));
							}
						}
						
					}
				}
				
			}
			// Z方向
			sp = innerSupportPathZ;
			if(sp.Count > 0){
				for(int l = 0; l < sp.Count; l++){
					if(sp[l] == null){continue;}
					if(sp[l].Count > 0){
						// まずスタート位置に
						Gcode.Add("G1" + toGcodeXY(sp[l][0].v));
						Gcode.Add("G1" + " E" + (RETRACT_AMOUNT) + " F" + SPEED_MOVE+ " ; Re-retraction!");
						for(int si = 0; si < sp[l].Count-1; si++){
							if(sp[l][si].label){
								Gcode.Add("G1" + toGcodeCalcE(sp[l][si].v, sp[l][si+1].v));
							}else{
								Gcode.Add("G1" + toGcodeXY(sp[l][si+1].v));
							}
						}
						Gcode.Add("G1" + " E" + (RETRACT_AMOUNT*-1) + " F" + SPEED_MOVE+ " ; Re-retraction!");
						
					}
				}
			}
		}
		
		// Gcodeの生成のためのメソッド ======================================================
		
		
		// Gcodeの生成 ======================================================
		private void createGcode(int sn){
			
			double currentheight = Start_Height + sn *LAYER_HEIGHT;
			Gcode = new List<string>();
			double pitch = 0;
			
			Gcode.Add("; Layer : " +sn+ "==========================");
			
			// サポート構造 =========================================
			#region サポートのGcode生成
			FLogger.Log(LogType.Debug, " Gc == サポートのGcodeを生成します");
			Gcode.Add("; Support structure ==========================");
			// サポート構造の生成
			// 1層目だけここに入る
			if(innerSupportPathX != null && innerSupportPathZ != null ){
				
				pitch = currentheight;
				for(int i = 0; i < SUPPORT_LAYER_COUNT; i++){
					// サポートの積層ピッチ
					pitch = (LAYER_HEIGHT / SUPPORT_LAYER_COUNT) * (i+1) ;
					
					Gcode.Add("G1 Z" + (LAYER_HEIGHT*sn+pitch).ToString("F2") + " F" + SPEED_ZIGZAG_EXTRUDE);
					supportGcodeSub();
				}
				
			}
			#endregion
			
			// ZIGZAG構造 =========================================
			#region ZIGZAGのGcode生成
			FLogger.Log(LogType.Debug, " Gc == ZigzagのGcodeを生成します");
			Gcode.Add("; Zigzag structure ==========================");
			
			for(int i = 0; i < outerPath[sn].Count; i++){
				if(outerPath[sn][i].Count < 2){ continue; }
				// まずスタート位置へ移動
				Gcode.Add("G1 Z" + (currentheight).ToString("F2") + " F" + SPEED_ZIGZAG_EXTRUDE);
				// Retraction!
				// スタート地点が細くなる場合は，ここの値を増やせば良い？
				Gcode.Add("G1" + " E" + (RETRACT_AMOUNT) + " F" + SPEED_RETRACTION+ " ; Re-retraction!");
				Gcode.Add("G1" + toGcodeXY(outerPath[sn][i][0]));
				
				// 一周する
				for(int j = 0; j < outerPath[sn][i].Count; j++){
					Gcode.Add("G1" + toGcodeZigzagCalcE(outerPath[sn][i][j],outerPath[sn][i][(j+1)%outerPath[sn][i].Count]));
				}
				
				// traction!
				Gcode.Add("G1" + " E" + (RETRACT_AMOUNT * -1) + " F" + SPEED_RETRACTION+" ; Retraction!");
				// 終点を押しつぶす
				Gcode.Add("G1 Z" + (LAYER_HEIGHT*sn+pitch).ToString("F2") + " F" + SPEED_ZIGZAG_EXTRUDE);
				
			}
			#endregion
			
			// ZIGZAG構造の内部 =========================================
			#region ZIGZAGのGcode生成
			for(int i = 0; i < innerPathX.Length; i++){
				if(innerPathX[i] == null){continue;}
				if(innerPathX[i].Count > 0){
					// まずスタート位置に
					Gcode.Add("G1" + toGcodeXY(innerPathX[i][0].v));
					// 開始位置に
					Gcode.Add("G1 Z" + (currentheight).ToString("F2") + " F" + SPEED_ZIGZAG_EXTRUDE);
					for(int si = 0; si < innerPathX[i].Count-1; si++){
						if(innerPathX[i][si].label){
							Gcode.Add("G1" + " E" + (RETRACT_AMOUNT) + " F" + SPEED_RETRACTION+ " ; Re-retraction!");
							Gcode.Add("G1" + toGcodeZigzagCalcE(innerPathX[i][si].v, innerPathX[i][si+1].v));
							// traction!
							Gcode.Add("G1" + " E" + (RETRACT_AMOUNT * -1) + " F" + SPEED_RETRACTION+" ; Retraction!");
						}else{
							Gcode.Add("G1" + toGcodeXY(innerPathX[i][si+1].v));
						}
					}
					// 終点を押しつぶす
					Gcode.Add("G1 Z" + (LAYER_HEIGHT*sn+pitch).ToString("F2") + " F" + SPEED_ZIGZAG_EXTRUDE);
				}
			}
			#endregion
			
			
			Gcode.Add("; END\n");
			
			FGcode.SliceCount = Gcode.Count;
			FGcode.AssignFrom(Gcode);
			
		}
		
		
		//===============================================================================================
		//called when data for any output pin is requested ==============================================
		//===============================================================================================
		
		public void Evaluate(int SpreadMax)
		{
			FDoWrite[0] = false;
			
			// 処理中はこっちを優先
			if(createFlag){
				FMes[0] = "Processing...";
				if(Current_Slice >= Slice_Num){
					// モデルの高さを超えたらおしまい
					FMes[0] = "おわり";
					initFlag = false;
					createFlag = false;
					FDoneInit[0] = false;
					FLogger.Log(LogType.Debug, " ===== 処理 終わり ===== ");
					return;
				}
				
				FLogger.Log(LogType.Debug, " ===== ループ処理 " + Current_Slice + " 回目 ===== ");
				
				// 内部構造を作る
				createInner(Current_Slice);
				//createInner(FD[0]);
				
				// Gcodeを生成して出力
				createGcode(Current_Slice);
				
				// サポート材あり
				Current_Slice++;
				FDoWrite[0] = true;
			}
			else{
				if(FReset[0]){
					FLogger.Log(LogType.Debug, " ===== 初期化 開始 ===== ");
					
					// 初期化
					init();
					
					// 事前に外周を作る
					int sc = 0;
					for(double d = BASE_HEIGHT; MODEL_HEIGHT > d; d += LAYER_HEIGHT){
						createOuter(d);
						outerPath[sc++] = oneOuterRoad;
					}
					
					// サポート部分を整理
					// 上から順に見ていくが，一番上は除く
					createSupport();
					
					// デバッグ出力 =================================================================
					FOutAllPoints.SliceCount = 0;
					FOutPointsBin.SliceCount = 0;
					int allpointscount = 0;
					
					for(int i = 0; i < outerPath.Length; i++){
						FOutPointsBin.SliceCount++;
						FOutPointsBin[FOutPointsBin.SliceCount-1] = 0;
						for(int j = 0; j < outerPath[i].Count; j++){
							FOutPointsBin[FOutPointsBin.SliceCount-1] += outerPath[i][j].Count*3;
							FOutAllPoints.SliceCount += outerPath[i][j].Count;
							for(int k = 0; k < outerPath[i][j].Count; k++){
								FOutAllPoints[allpointscount++] = outerPath[i][j][k];
							}
						}
					}
					
					FOutSupAllPoints.SliceCount = 0;
					FOutSupPointsBin.SliceCount = 0;
					int allsuppointscount = 0;
					
					for(int i = 0; i < supportPath.Length; i++){
						FOutSupPointsBin.SliceCount++;
						FOutSupPointsBin[FOutSupPointsBin.SliceCount-1] = 0;
						if(supportPath[i] == null){continue;}
						for(int j = 0; j < supportPath[i].Count; j++){
							FOutSupPointsBin[FOutSupPointsBin.SliceCount-1] += supportPath[i][j].Count*3;
							FOutSupAllPoints.SliceCount += supportPath[i][j].Count;
							for(int k = 0; k < supportPath[i][j].Count; k++){
								FOutSupAllPoints[allsuppointscount++] = supportPath[i][j][k];
							}
							if(i == 10)
							FLogger.Log(LogType.Debug, "   "+supportPath[i][j].Count);
						}
					}
					
					// サポートまでの距離
					FLogger.Log(LogType.Debug, "サポートまでの距離： "+LAYER_HEIGHT / Math.Tan(radians(OVER_HANG)));
					FLogger.Log(LogType.Debug, "サポートの層の数： "+ SUPPORT_LAYER_COUNT);
					
					// デバッグ出力 =================================================================
					FLogger.Log(LogType.Debug, " ===== 初期化 終了 ===== ");
					FMes[0] = "初期化完了";
					initFlag = true;
				}
				if(FStart[0] && initFlag){
					// 層との処理開始
					FDoneInit[0] = true;
					createFlag = true;
				}
			}
		}
	}
	
	// Faceクラス
	// 今回はVector3D で作ってみる
	class Face{
		public Vector3D p1,p2,p3,n; // 頂点
		public Face adjF12,adjF23,adjF31; // 隣接する面
		public Vector3D cp12,cp23,cp31;
		public int mark; // 連結確認用
		// -1: 交差しない 0: 未チェック 1: チェック済み
		
		public Face(Vector3D p1, Vector3D p2, Vector3D p3){
			this.p1 = p1;
			this.p2 = p2;
			this.p3 = p3;
			this.n = (p2-p1).CrossRH(p3-p2) / (p2-p1).CrossRH(p3-p2).Length;
			
			this.mark = -1;
		}
		
		// とりあえず交差判定だけ
		public bool isCross(double Y){
			// 面の頂点がすべて上か下にある
			// これでいいのかな？
			if(p1.y > Y && p2.y > Y && p3.y > Y || p1.y < Y && p2.y < Y && p3.y < Y){
				return false;
			}
			else{
				return true;
			}
		}
		
		// 隣接する平面が決まっているかどうか
		// 決まっていればTrue
		public bool checkNeighborsFace(){
			if(adjF12 == null || adjF23 == null || adjF31 == null){
				return false;
			}else{
				return true;
			}
		}
		
		// その頂点が面を構成する1点になっているか
		public int hasPoint(Vector3D v){
			if(p1 == v || p2 == v || p3 == v){
				return 1;
			}else{
				return 0;
			}
		}
		
		// 面の隣接判定と連結
		public bool connectNeighbor(Face tf){
			// まず同じ点を2つ持っているか
			int ch = tf.hasPoint(p1) + tf.hasPoint(p2) + tf.hasPoint(p3);
			if(ch != 2){ return false; }
			
			// これで全部処理できてる？
			if(tf.hasPoint(p1) == 1){
				if(tf.hasPoint(p2) == 1){
					this.adjF12 = tf;
				}
				if(tf.hasPoint(p3) == 1){
					this.adjF31 = tf;
				}
			}
			else if(tf.hasPoint(p2) == 1){
				if(tf.hasPoint(p3) == 1){
					this.adjF23 = tf;
				}
			}
			
			return true;
		}
		
		// 2頂点を渡された時に中点を返す
		// 面が渡される点を持っている前提で呼ぶ
		public Vector3D getMidPoint(Vector3D v1, Vector3D v2){
			if(p1 == v1 && p2 == v2 || p1 == v2 && p2 == v1){
				return cp12;
			}
			else if(p2 == v1 && p3 == v2 || p3 == v2 && p2 == v1){
				return cp23;
			}
			else{
				//  if(p3 == v1 && p1 == v2 || p1 == v2 && p3 == v1)
				return cp31;
			}
		}
		
		private double dot(Vector3D v1, Vector3D v2){
			return v1.x*v2.x + v1.y*v2.y + v1.z*v2.z;
		}
		
		// v が内部にあるかどうか
		public bool hasInside(Vector3D v){
			double d1Dot =  dot((p2-p1).CrossRH(v-p1), this.n);
			double d2Dot =  dot((p3-p2).CrossRH(v-p2), this.n);
			double d3Dot =  dot((p1-p3).CrossRH(v-p3), this.n);
			
			if(d1Dot * d2Dot > 0 && d2Dot * d3Dot > 0){
				return true;
			}
			return false;
		}
	}
}

// label
// true が入り口，false が出口
class PointWithLabel{
	public Vector3D v;
	public bool label;
	
	public PointWithLabel(Vector3D v, bool l){
		this.v = v;
		this.label = l;
	}
}