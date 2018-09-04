using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

//キャプチャのために必要なusingを宣言
using Windows.Media.Capture;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.AI.MachineLearning;
using Windows.Storage;
using Windows.Storage.Streams;

// 空白ページの項目テンプレートについては、https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x411 を参照してください

namespace UWP_MobileNet_Demo
{
	/// <summary>
	/// それ自体で使用できる空白ページまたはフレーム内に移動できる空白ページ。
	/// </summary>
	public sealed partial class MainPage : Page
	{
		//メンバ変数の宣言
		MediaCapture mediaCapture;		//キャプチャインスタンス
		Model ModelGen;					//DNNモデル用の変数を宣言
		ILSVRC2012Classes classList;	//Imagenetのクラス

		//並列処理の実行抑制のためのセマフォ
		private System.Threading.SemaphoreSlim semaphore = new System.Threading.SemaphoreSlim(1);                     //複数のスレッドで検出しないようにするためのsemaphore

		//周期呼び出し処理
		//DispatcherTimer Timer_capture;
		System.Threading.Timer Timer_capture;

		public MainPage()
		{
			this.InitializeComponent();

			//webカメラキャプチャのための必要なインスタンスの初期化
			mediaCapture = new MediaCapture();

			//カメラ取得失敗時のイベントハンドラを実装
			mediaCapture.Failed += new MediaCaptureFailedEventHandler(MediaCapture_Failed);
		}

		/// <summary>
		/// カメラ取得失敗時のイベント
		/// </summary>
		/// <param name="e"></param>
		static async void MediaCapture_Failed(MediaCapture c, MediaCaptureFailedEventArgs e)
		{
			await new Windows.UI.Popups.MessageDialog("カメラの取得に失敗しました", "エラー").ShowAsync();
		}

		/// <summary>
		/// カメラ初期化（非同期）
		/// </summary>
		/// <returns></returns>
		private async Task InitWebCameraAsync()
		{
			try
			{
				//mediaCaptureオブジェクトが有効な時は一度Disposeする
				if (mediaCapture != null)
				{
					mediaCapture.Dispose();
					mediaCapture = null;
				}

				//キャプチャーの設定情報を保持するインスタンスを作成
				var captureInitSettings = new MediaCaptureInitializationSettings();
				captureInitSettings.VideoDeviceId = "";
				captureInitSettings.StreamingCaptureMode = StreamingCaptureMode.Video;

				//カメラデバイスの取得
				var cameraDevices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
					Windows.Devices.Enumeration.DeviceClass.VideoCapture);

				//取得結果に応じて分岐
				if (cameraDevices.Count() == 0)		//取得デバイスなしの場合
				{
					Debug.WriteLine("No Camera");	//デバッグ文字列出力
					return;		//カメラがない場合は関数から出る
				}
				else if (cameraDevices.Count() == 1)	//1個だけ見つかったとき
				{
					Debug.WriteLine("count1\n");	//デバッグ用のメッセージ
					captureInitSettings.VideoDeviceId = cameraDevices[0].Id;	//見つけたデバイスIDを設定
				}
				else			//複数見つけたとき
				{
					Debug.WriteLine("countelse\n");
					captureInitSettings.VideoDeviceId = cameraDevices[1].Id;	//2個目のデバイスIDを設定
				}

				//キャプチャーの準備
				mediaCapture = new MediaCapture();		//キャプチャインスタンスを作成
				await mediaCapture.InitializeAsync(captureInitSettings);    //キャプチャ設定に従って設定を初期化する

				//ビデオストリームのエンコードに関する設定
				Windows.Media.MediaProperties.VideoEncodingProperties vp = 
					new Windows.Media.MediaProperties.VideoEncodingProperties();

				Debug.WriteLine("before camera size\n");
				//RasperryPiでは解像度が高いと映像が乱れるので小さい解像度にしている
				//ラズパイじゃなければ必要ないかも？
				/*vp.Width = 640;
				vp.Height = 480;
				vp.Subtype = "RGB24";

				await mediaCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, vp);*/

				//コントロールにキャプチャインスタンスをセット
				CaptureElement_webcam.Source = mediaCapture;

				//キャプチャーの開始
				await mediaCapture.StartPreviewAsync();

				Debug.WriteLine("Camera Initialized");

				//指定周期のタイマを作成・起動
				//コールバック関数にキャプチャインスタンスを渡す、遅延0ms、周期500ms
				this.Timer_capture = new System.Threading.Timer(
					new System.Threading.TimerCallback(TimerCapFlame),
					mediaCapture,0,500);
			}
			catch (Exception ex)
			{
				Debug.Write(ex.Message);
			}
		}

		/// <summary>
		/// タイマイベント
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private async void TimerCapFlame(object sender)
		{
			//複数スレッドでの同時実行を抑制
			if (!semaphore.Wait(0))
			{
				return;
			}
			else if(this.ModelGen == null)
			{
				semaphore.Release();
				return;
			}

			try
			{
				//AIモデルのインプットデータは解像度224x224,BGRA8にする必要がある。
				BitmapPixelFormat InputPixelFormat = BitmapPixelFormat.Bgra8;
				using (VideoFrame previewFrame = new VideoFrame(InputPixelFormat, 640, 480, BitmapAlphaMode.Ignore))
				{
					//フレームを取得
					await this.mediaCapture.GetPreviewFrameAsync(previewFrame);

					if (previewFrame != null)       //フレームを正しく取得できた時
					{
						//モデルへのデータ入力クラスでインスタンスを作成する
						var modelInput = new Input();
						
						//SoftwareBitmapを作成
						SoftwareBitmap bitmapBuffer = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 224, 224, BitmapAlphaMode.Ignore);
						
						//SoftwareBitmapでVideoFrameを作成する
						VideoFrame buffer = VideoFrame.CreateWithSoftwareBitmap(bitmapBuffer);

						//キャプチャしたフレームを作成したVideoFrameへコピーする
						await previewFrame.CopyToAsync(buffer);

						//SoftwareBitmapを取得する（これはリサイズ済みになる）
						SoftwareBitmap resizedBitmap = buffer.SoftwareBitmap;

						//WritableBitmapへ変換する
						WriteableBitmap innerBitmap = null;
						byte[] buf = null;
						await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () => {
								innerBitmap = new WriteableBitmap(resizedBitmap.PixelWidth, resizedBitmap.PixelHeight);

								resizedBitmap.CopyToBuffer(innerBitmap.PixelBuffer);
								buf = new byte[innerBitmap.PixelBuffer.Length];
								innerBitmap.PixelBuffer.CopyTo(buf);
							});
						
						//バッファへコピーする
						//innerBitmap.PixelBuffer.CopyTo(buf);6
						SoftwareBitmap sb = SoftwareBitmap.CreateCopyFromBuffer(buf.AsBuffer(), BitmapPixelFormat.Bgra8, 224,224, BitmapAlphaMode.Ignore);
						
						//取得画像をコントロールに表示する
						await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
						{
							var src = new SoftwareBitmapSource();
							//await src.SetBitmapAsync(previewFrame.SoftwareBitmap);
							await src.SetBitmapAsync(sb);
							Image_CapImg.Source = src;
						});
						
						//画像のアルファチャンネル削除と配列形状変更
						byte[] buf2 = ConvertImageaArray(buf);
						

						//正規化しつつfloat配列に変換する
						float[] inData = NormalizeImage(buf2);

						//入力用のテンソルを作成（Windows.AI.MachineLearning.TensorFloatクラス）
						TensorFloat tf =
							TensorFloat.CreateFromArray(new long[] { 1, 3, 224, 224 }, inData);

						//入力フォーマットに合わせたデータをセットする
						Input indata = new Input();
						indata.data = tf;
						modelInput.data = tf;

						//AIモデルにデータを渡すと推定値の入ったリストが返る
						//ModelOutput = await ModelGen.EvaluateAsync(modelInput);
						var output = await ModelGen.EvaluateAsync(modelInput);

						//UIスレッドに結果を表示
						await this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
						{
							//予測結果を表示
							//string label = outputData.classLabel[0];
							var result_vec = output.mobilenetv20_output_flatten0_reshape0.GetAsVectorView();
							var list = result_vec.ToArray<float>();
							var max1st = list.Max();
							var index1st = Array.IndexOf(list, max1st);     //最大確立のインデックスを取得

							string ans = classList.Classes[index1st].ToString();
							//result = result + "Class: " + label + ", Prob: " + ModelOutput.prob_1[label];

							//結果表示
							this.Text_Result_1st.Text = ans + ":" + max1st.ToString("0.0");
						});
					}
				}
			}
			catch(Exception ex)
			{
				Debug.WriteLine("周期処理で例外発生");
				Debug.WriteLine(ex.ToString());
			}
			finally
			{
				semaphore.Release();
			}
		}

		/// <summary>
		/// モデルをロードする
		/// </summary>
		/// <returns></returns>
		private async Task LoadOnnxModel()
		{
			try
			{
				//モデルをロードする
				Windows.Storage.StorageFile modelFile =
					await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(
						new Uri("ms-appx:///Assets/mobilenetv2-1.0.onnx"));        //Assetsからonnxモデルをロード
				this.ModelGen = await Model.CreateFromStreamAsync(modelFile);
				Debug.Print("モデルロード完了");
			}
			catch (Exception e)
			{
				Debug.Print(e.ToString());
			}
			classList = new ILSVRC2012Classes();	//クラスリストをロードする
		}

		/// <summary>
		/// 画像の行列を[*,*,4]からアルファを消して、さらに[3,*,*]の並びに変換する
		/// </summary>
		/// <param name="src"></param>
		/// <returns></returns>
		private byte[] ConvertImageaArray(byte[] src)
		{
			//戻り値用の配列を準備
			byte[] res = new byte[(src.Length / 4) * 3];

			int offset_b = 0;
			int offset_g = src.Length / 4;
			int offset_r = src.Length / 2;

			//アルファチャンネルが邪魔なので、無理やり消す
			int j = 0;
			for (int i = 0; i < src.Length; i += 4)
			{
				res[offset_b + j] = src[i];
				res[offset_g + j] = src[i + 1];
				res[offset_r + j] = src[i + 2];
				j += 1;
			}
			return res;
		}

		/// <summary>
		/// 画像の正規化処理
		/// </summary>
		/// <param name="src"></param>
		/// <returns></returns>
		private float[] NormalizeImage(byte[] src)
		{
			float[] normalized = new float[src.Length];
			/*System.Threading.Tasks.Parallel.For(0, src.Length,i =>
			{
				normalized[i] = (float)src[i] / (float)255;
			});*/
			for (int i = 0; i < src.Length; i++)
			{
				normalized[i] = src[i] / (float)255.0;
			}
			return normalized;
		}

		/// <summary>
		/// ロード完了時のイベント
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private async void Page_Loaded(object sender, RoutedEventArgs e)
		{
			await InitWebCameraAsync();     //カメラ初期化
			await LoadOnnxModel();			//モデルをロード
		}
	}
}
