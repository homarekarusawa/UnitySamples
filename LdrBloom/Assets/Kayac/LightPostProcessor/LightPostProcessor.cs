﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Kayac
{
	public class LightPostProcessor : MonoBehaviour
	{
		[SerializeField]
		Shader _extractionShader;
		[SerializeField]
		Shader _blurShader;
		[SerializeField]
		Shader _combineShader;
		[SerializeField]
		Shader _compositionShader;

		[SerializeField]
		Vector3 _colorOffset = new Vector3(0f, 0f, 0f);
		[SerializeField]
		Vector3 _colorScale = new Vector3(1f, 1f, 1f);
		[SerializeField]
		float _saturation = 1f;
		[SerializeField]
		float _bloomPixelThreshold = 0;
		[SerializeField]
		int _bloomStartLevel = 2;
		[SerializeField]
		int _maxBloomLevelCount = 7;
		[SerializeField]
		int _minBloomLevelSize = 16;
		[SerializeField]
		float _bloomStrength = 1f;
		[SerializeField]
		float _bloomStrengthMultiplier = 2f;
		[SerializeField]
		float _bloomSigmaInPixel = 3f;

		Material _extractionMaterial;
		Material _blurMaterial;
		Material _combineMaterial;
		Material _compositionMaterial;
		RenderTexture _prevSource;
		RenderTexture _brightness;
		RenderTexture _bloomX;
		RenderTexture _bloomXY;
		RenderTexture _bloomCombined;
		List<BloomRect> _bloomRects;
		BloomSample[] _bloomSamples;
		bool _first = true; // クリアが必要かどうかのために初期化直後かどうかを記録
		readonly Color _clearColor = new Color(0f, 0f, 0f, 1f);
		bool _combineEnabled = true;
		public void ToggleCombine()
		{
			_combineEnabled = !_combineEnabled;
	Debug.Log(_combineEnabled);
		}

		public IEnumerable<RenderTexture> EnumerateRenderTexturesForDebug()
		{
			yield return _brightness;
			yield return _bloomX;
			yield return _bloomXY;
			yield return _bloomCombined;
		}

		void Start()
		{
			_maxBloomLevelCount = System.Math.Min(_maxBloomLevelCount, 7); // 最大7。シェーダ的な都合で。
			if (_blurMaterial == null)
			{
				_blurMaterial = new Material(_blurShader);
			}
			if (_extractionMaterial == null)
			{
				_extractionMaterial = new Material(_extractionShader);
			}
			if (_combineMaterial == null)
			{
				_combineMaterial = new Material(_combineShader);
			}
			if (_compositionMaterial == null)
			{
				_compositionMaterial = new Material(_compositionShader);
				SetColorTransform();
			}
			_bloomSamples = new BloomSample[4];
		}

		void OnRenderImage(RenderTexture source, RenderTexture destination)
		{
			SetupRenderTargets(source);

			GL.PushMatrix();
			GL.LoadIdentity();
			GL.LoadOrtho();

			bool clear = _first;
			int brightnessNetWidth = source.width >> _bloomStartLevel;
			int brightnessNetHeight = source.height >> _bloomStartLevel;
			int brightnessOffsetX = (_brightness.width - brightnessNetWidth) / 2; // 中央に配置する。端に置くと次のgaussianで末端がおかしくなる
			int brightnessOffsetY = (_brightness.height - brightnessNetHeight) / 2;
			ExtractBrightness(
				source,
				brightnessOffsetX,
				brightnessOffsetY,
				brightnessNetWidth,
				brightnessNetHeight,
				clear: true); // とりあえずクリア。TODO: 初回のみで良いGPUでは無駄にクリアしたくない
			// 係数再計算
			CalcGaussianSamples(_bloomSigmaInPixel);
			BlurX( // _gaussAの所定の場所へ_brightnessの各レベルからX方向ガウシアンブラー
				brightnessOffsetX,
				brightnessOffsetY,
				brightnessNetWidth,
				brightnessNetHeight);
			BlurY(); // _bloomX -> _bloomXY Y方向ガウシアンブラー
			if (_combineEnabled)
			{
				CombineBlur();
			}
			Composite(source, destination); // 最終合成
			GL.PopMatrix();
			_first = false;
		}

		void ExtractBrightness(
			RenderTexture source,
			int brightnessOffsetX,
			int brightnessOffsetY,
			int brightnessNetWidth,
			int brightnessNetHeight,
			bool clear)
		{
			/* 輝度抽出
			color' = (color - threshold) / (1 - threshold)
			とするのだが、高速に計算するために式を展開して積和の形にしておく
			  = (1/(1-threshold)) * color + (-threshold)/(1-threshold)
			*/
			_extractionMaterial.SetTexture("_MainTex", source);
			if (_bloomPixelThreshold <= 0f)
			{
				_extractionMaterial.EnableKeyword("PASS_THROUGH");
			}
			else
			{
				_extractionMaterial.DisableKeyword("PASS_THROUGH");
				var colorTransform = new Vector4(
					1f / (1f - _bloomPixelThreshold), // 乗算項
					-_bloomPixelThreshold / (1f - _bloomPixelThreshold), // 加算項
					0f,
					0f);
				_extractionMaterial.SetVector("_ColorTransform", colorTransform);
			}
			_extractionMaterial.SetPass(0);
			bool first = _first;
			Blit(
				source,
				0,
				0,
				source.width,
				source.height,
				_brightness,
				brightnessOffsetX,
				brightnessOffsetY,
				brightnessNetWidth,
				brightnessNetHeight,
				clear,
				_clearColor);
		}

		void BlurX(
			int brightnessOffsetX,
			int brightnessOffsetY,
			int brightnessNetWidth,
			int brightnessNetHeight)
		{
			// _gaussAの所定の場所へ_brightnessの各レベルからX方向ガウシアンブラー
			_brightness.filterMode = FilterMode.Bilinear; // バイリニアが必要
			_blurMaterial.SetTexture("_MainTex", _brightness);
			_blurMaterial.SetFloat(
				"_InvertOffsetScale01",
				(_bloomSamples[0].offset * 2f) / Mathf.Abs(_bloomSamples[0].offset - _bloomSamples[1].offset));
			//Debug.Log((_bloomSamples[0].offset * 2f) / Mathf.Abs(_bloomSamples[0].offset - _bloomSamples[1].offset));
			_blurMaterial.SetPass(0);
			_bloomX.DiscardContents();
			Graphics.SetRenderTarget(_bloomX);
			GL.Clear(false, true, _clearColor);
			int w = _brightness.width; // 各ミップレベルの幅
			GL.Begin(GL.QUADS);
			for (int i = 0; i < _bloomRects.Count; i++)
			{
				var rect = _bloomRects[i];
				AddBlurQuads(
					_brightness,
					brightnessOffsetX,
					brightnessOffsetY,
					brightnessNetWidth,
					brightnessNetHeight,
					1f / (float)w,
					_bloomX,
					rect.x,
					rect.y,
					rect.width,
					rect.height,
					forX: true);
				w /= 2;
			}
			GL.End();
		}

		void BlurY()
		{
			_bloomXY.DiscardContents();
			Graphics.SetRenderTarget(_bloomXY);
			GL.Clear(false, true, _clearColor);
			_bloomX.filterMode = FilterMode.Bilinear; // バイリニアが必要
			_blurMaterial.SetTexture("_MainTex", _bloomX);
			_blurMaterial.SetPass(0);
			GL.Begin(GL.QUADS);
			for (int i = 0; i < _bloomRects.Count; i++)
			{
				var rect = _bloomRects[i];
				AddBlurQuads(
					_bloomX,
					rect.x,
					rect.y,
					rect.width,
					rect.height,
					1f / (float)_bloomX.height,
					_bloomXY,
					rect.x,
					rect.y,
					rect.width,
					rect.height,
					forX: false);
			}
			GL.End();
		}

		// _bloomXYの各解像度を2つづつ組にして加算する
		void CombineBlur()
		{
			_bloomCombined.DiscardContents();
			Graphics.SetRenderTarget(_bloomCombined);
			GL.Clear(false, true, _clearColor);
			_bloomXY.filterMode = FilterMode.Bilinear;
			_combineMaterial.SetTexture("_MainTex", _bloomXY);
			_combineMaterial.SetPass(0);
			// 小さい方から2枚づつ合成
			GL.Begin(GL.QUADS);
			int i = 0;

			while (i < _bloomRects.Count)
			{
				// レベル数が奇数の場合、最初のレベルは単純コピーとする。DrawCallを増やしたくないので同じ矩形を2回tex2Dして半分にして吐く。無駄だが許容する。たぶん問題にならない。
				var rect0 = _bloomRects[i];
				var rect1 = _bloomRects[i + 1];
				var weight0 = 1f;
				var weight1 = _bloomStrengthMultiplier;
				// 正規化して、現段階で真っ白だった時にあふれないようにする
				var weightSum = weight0 + weight1;
				weight0 /= weightSum;
				weight1 /= weightSum;
				var advanceI = 2;
				if ((i == 0) && ((_bloomRects.Count % 2) != 0))
				{
					rect1 = rect0;
					advanceI = 1; // 1しか進めない
				}
				float x0 = (float)rect0.x / (float)_bloomCombined.width;
				float x1 = (float)(rect0.x + rect0.width) / (float)_bloomCombined.width;
				float y0 = (float)rect0.y / (float)_bloomCombined.height;
				float y1 = (float)(rect0.y + rect0.height) / (float)_bloomCombined.height;

				float u00 = x0;
				float u10 = x1;
				float v00 = y0;
				float v10 = y1;

				float u01 = (float)rect1.x / (float)_bloomCombined.width;
				float u11 = (float)(rect1.x + rect1.width) / (float)_bloomCombined.width;
				float v01 = (float)rect1.y / (float)_bloomCombined.height;
				float v11 = (float)(rect1.y + rect1.height) / (float)_bloomCombined.height;

				GL.MultiTexCoord3(0, u00, v00, weight0);
				GL.MultiTexCoord3(1, u01, v01, weight1);
				GL.Vertex3(x0, y0, 0f);

				GL.MultiTexCoord3(0, u00, v10, weight0);
				GL.MultiTexCoord3(1, u01, v11, weight1);
				GL.Vertex3(x0, y1, 0f);

				GL.MultiTexCoord3(0, u10, v10, weight0);
				GL.MultiTexCoord3(1, u11, v11, weight1);
				GL.Vertex3(x1, y1, 0f);

				GL.MultiTexCoord3(0, u10, v00, weight0);
				GL.MultiTexCoord3(1, u11, v01, weight1);
				GL.Vertex3(x1, y0, 0f);

				i += advanceI;
			}
			GL.End();
		}

		void Composite(RenderTexture source, RenderTexture destination)
		{
			// カラーフィルターの有効無効設定
			if ((_colorOffset == Vector3.zero)
				&& (_colorScale == Vector3.one)
				&& (_saturation == 1f))
			{
				_compositionMaterial.DisableKeyword("COLOR_FILTER");
			}
			else
			{
				_compositionMaterial.EnableKeyword("COLOR_FILTER");
			}
			int compositeBloomRectCount = _bloomRects.Count;
			if (_combineEnabled)
			{
				compositeBloomRectCount = (compositeBloomRectCount + 1) / 2;
			}
			if ((compositeBloomRectCount & 0x4) != 0)
			{
				_compositionMaterial.EnableKeyword("BLOOM_4");
			}
			else
			{
				_compositionMaterial.DisableKeyword("BLOOM_4");
			}
			if ((compositeBloomRectCount & 0x2) != 0)
			{
				_compositionMaterial.EnableKeyword("BLOOM_2");
			}
			else
			{
				_compositionMaterial.DisableKeyword("BLOOM_2");
			}
			if ((compositeBloomRectCount & 0x1) != 0)
			{
				_compositionMaterial.EnableKeyword("BLOOM_1");
			}
			else
			{
				_compositionMaterial.DisableKeyword("BLOOM_1");
			}


			source.filterMode = FilterMode.Point; // ポイントで良い
			_compositionMaterial.SetTexture("_MainTex", source);
			// 強度定数を計算する
			float s = 1f;
			float sSum = 0f;
			int i;
			for (i = 0; i < _bloomRects.Count; i++)
			{
				sSum += s;
				s *= _bloomStrengthMultiplier;
			}
			float strengthBase = _bloomStrength / sSum;
			float strength = strengthBase;
			if (_combineEnabled)
			{
				_bloomCombined.filterMode = FilterMode.Bilinear; // バイリニアが必要
				_compositionMaterial.SetTexture("_BloomTex", _bloomCombined);
				float antiNormalizeFactor = 1f + _bloomStrengthMultiplier;
				i = 0;
				var propertyIndex = 0;
				while (i < _bloomRects.Count)
				{
					var rect = _bloomRects[i]; // 1個飛ばしになる。飛ばされたものはもうCombine段で足し合わされている
					_compositionMaterial.SetFloat(_bloomRects[propertyIndex].weightShaderPropertyId, strength * antiNormalizeFactor);
					Vector4 uvTransform;
					uvTransform.x = (float)rect.width / (float)_bloomXY.width;
					uvTransform.y = (float)rect.height / (float)_bloomXY.height;
					uvTransform.z = (float)rect.x / (float)_bloomXY.width;
					uvTransform.w = (float)rect.y / (float)_bloomXY.height;
					_compositionMaterial.SetVector(_bloomRects[propertyIndex].uvTransformShaderPropertyId, uvTransform);
					// 最初のレベルで、全体が奇数なら、1つしか進めない
					if ((i == 0) && ((_bloomRects.Count % 2) != 0))
					{
						strength *= _bloomStrengthMultiplier;
						i += 1;
					}
					else // その他は2段づつ進む
					{
						strength *= _bloomStrengthMultiplier;
						strength *= _bloomStrengthMultiplier;
						i += 2;
					}
					++propertyIndex;
				}
			}
			else
			{
				_bloomXY.filterMode = FilterMode.Bilinear; // バイリニアが必要
				_compositionMaterial.SetTexture("_BloomTex", _bloomXY);
				for (i = 0; i < _bloomRects.Count; i++)
				{
					var rect = _bloomRects[i];
					_compositionMaterial.SetFloat(rect.weightShaderPropertyId, strength);
					Vector4 uvTransform;
					uvTransform.x = (float)rect.width / (float)_bloomXY.width;
					uvTransform.y = (float)rect.height / (float)_bloomXY.height;
					uvTransform.z = (float)rect.x / (float)_bloomXY.width;
					uvTransform.w = (float)rect.y / (float)_bloomXY.height;
					_compositionMaterial.SetVector(rect.uvTransformShaderPropertyId, uvTransform);
					strength *= _bloomStrengthMultiplier;
				}
			}
			_compositionMaterial.SetPass(0);

			if (destination != null)
			{
				destination.DiscardContents();
			}
			Graphics.SetRenderTarget(destination);
			GL.Begin(GL.QUADS);
			GL.TexCoord2(0f, 0f);
			GL.Vertex3(0f, 0f, 0f);
			GL.TexCoord2(0f, 1f);
			GL.Vertex3(0f, 1f, 0f);
			GL.TexCoord2(1f, 1f);
			GL.Vertex3(1f, 1f, 0f);
			GL.TexCoord2(1f, 0f);
			GL.Vertex3(1f, 0f, 0f);
			GL.End();
		}

		void AddBlurQuads(
			RenderTexture from,
			int fromX,
			int fromY,
			int fromWidth,
			int fromHeight,
			float offsetScale,
			RenderTexture to,
			int toX,
			int toY,
			int toWidth,
			int toHeight,
			bool forX)
		{
			float x0 = (float)toX / (float)to.width;
			float x1 = (float)(toX + toWidth) / (float)to.width;
			float y0 = (float)toY / (float)to.height;
			float y1 = (float)(toY + toHeight) / (float)to.height;

			float u0 = (float)fromX / (float)from.width;
			float u1 = (float)(fromX + fromWidth) / (float)from.width;
			float v0 = (float)fromY / (float)from.height;
			float v1 = (float)(fromY + fromHeight) / (float)from.height;

			float uOffset0 = _bloomSamples[0].offset * offsetScale;
			float vOffset0 = _bloomSamples[0].offset * offsetScale;
			float uOffset1 = _bloomSamples[1].offset * offsetScale;
			float vOffset1 = _bloomSamples[1].offset * offsetScale;
			float uOffset2 = _bloomSamples[2].offset * offsetScale;
			float vOffset2 = _bloomSamples[2].offset * offsetScale;
			float uOffset3 = _bloomSamples[3].offset * offsetScale;
			float vOffset3 = _bloomSamples[3].offset * offsetScale;
			if (forX)
			{
				vOffset0 = vOffset1 = vOffset2 = vOffset3 = 0f;
			}
			else
			{
				uOffset0 = uOffset1 = uOffset2 = uOffset3 = 0f;
			}
			//Debug.Log(from.name + " -> " + to.name + " " + x0 + "," + y0 + " - " + x1 + "," + y1 + " uv: " + u0 + "," + v0 + " - " + u1 + "," + v1 + " size: " + toWidth + "x" + toHeight + " / " + to.width + "x" + to.height);

			GL.MultiTexCoord3(0, u0 + uOffset0, v0 + vOffset0, _bloomSamples[0].weight);
			GL.MultiTexCoord3(1, u0 + uOffset1, v0 + vOffset1, _bloomSamples[1].weight);
			GL.MultiTexCoord3(2, u0 + uOffset2, v0 + vOffset2, _bloomSamples[2].weight);
			GL.MultiTexCoord3(3, u0 + uOffset3, v0 + vOffset3, _bloomSamples[3].weight);
			GL.Vertex3(x0, y0, 0f);

			GL.MultiTexCoord3(0, u0 + uOffset0, v1 + vOffset0, _bloomSamples[0].weight);
			GL.MultiTexCoord3(1, u0 + uOffset1, v1 + vOffset1, _bloomSamples[1].weight);
			GL.MultiTexCoord3(2, u0 + uOffset2, v1 + vOffset2, _bloomSamples[2].weight);
			GL.MultiTexCoord3(3, u0 + uOffset3, v1 + vOffset3, _bloomSamples[3].weight);
			GL.Vertex3(x0, y1, 0f);

			GL.MultiTexCoord3(0, u1 + uOffset0, v1 + vOffset0, _bloomSamples[0].weight);
			GL.MultiTexCoord3(1, u1 + uOffset1, v1 + vOffset1, _bloomSamples[1].weight);
			GL.MultiTexCoord3(2, u1 + uOffset2, v1 + vOffset2, _bloomSamples[2].weight);
			GL.MultiTexCoord3(3, u1 + uOffset3, v1 + vOffset3, _bloomSamples[3].weight);
			GL.Vertex3(x1, y1, 0f);

			GL.MultiTexCoord3(0, u1 + uOffset0, v0 + vOffset0, _bloomSamples[0].weight);
			GL.MultiTexCoord3(1, u1 + uOffset1, v0 + vOffset1, _bloomSamples[1].weight);
			GL.MultiTexCoord3(2, u1 + uOffset2, v0 + vOffset2, _bloomSamples[2].weight);
			GL.MultiTexCoord3(3, u1 + uOffset3, v0 + vOffset3, _bloomSamples[3].weight);
			GL.Vertex3(x1, y0, 0f);
		}

		void Blit(
			RenderTexture from,
			int fromX,
			int fromY,
			int fromWidth,
			int fromHeight,
			RenderTexture to,
			int toX,
			int toY,
			int toWidth,
			int toHeight,
			bool clear,
			Color clearColor)
		{
			float x0 = (float)toX / (float)to.width;
			float x1 = (float)(toX + toWidth) / (float)to.width;
			float y0 = (float)toY / (float)to.height;
			float y1 = (float)(toY + toHeight) / (float)to.height;

			float u0 = (float)fromX / (float)from.width;
			float u1 = (float)(fromX + fromWidth) / (float)from.width;
			float v0 = (float)fromY / (float)from.height;
			float v1 = (float)(fromY + fromHeight) / (float)from.height;
			//Debug.Log(from.name + " -> " + to.name + " " + x0 + "," + y0 + " - " + x1 + "," + y1 + " uv: " + u0 + "," + v0 + " - " + u1 + "," + v1 + " size: " + toWidth + "x" + toHeight + " / " + to.width + "x" + to.height);

			if (RenderTexture.active != to)
			{
				to.DiscardContents(); // 刺す前に必ずDiscard
				Graphics.SetRenderTarget(to);
			}
			if (clear)
			{
				GL.Clear(false, true, clearColor);
			}

			GL.Begin(GL.QUADS);
			GL.TexCoord2(u0, v0);
			GL.Vertex3(x0, y0, 0f);
			GL.TexCoord2(u0, v1);
			GL.Vertex3(x0, y1, 0f);
			GL.TexCoord2(u1, v1);
			GL.Vertex3(x1, y1, 0f);
			GL.TexCoord2(u1, v0);
			GL.Vertex3(x1, y0, 0f);
			GL.End();
		}

		public float bloomPixelThreshold
		{
			set
			{
				_bloomPixelThreshold = value;
			}
		}

		public float bloomStrength
		{
			set
			{
				_bloomStrength = value;
			}
		}

		public void SetColorFilter(Vector3 colorOffset, Vector3 colorScale, float saturation)
		{
			_colorOffset = colorOffset;
			_colorScale = colorScale;
			_saturation = saturation;
			SetColorTransform();
		}

		void SetupRenderTargets(RenderTexture source)
		{
			if (_prevSource != null)
			{
				if ((source != null)
					&& (source.width == _prevSource.width)
					&& (source.height == _prevSource.height))
				{
					return;
				}
			}
			if (_maxBloomLevelCount == 0)
			{
				return;
			}
#if UNITY_EDITOR
			_bloomStartLevelOnSetup = _bloomStartLevel;
			_maxBloomLevelCountOnSetup = _maxBloomLevelCount;
			_minBloomLevelSizeOnSetup = _minBloomLevelSize;
#endif
			var format = RenderTextureFormat.ARGB32;

#if false // 手持ちの京セラS2にて、GLES2でビルドすると絵が出なくなる。そもそもtrueを返すなよ...。ver2018.3.9
			if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGB2101010))
			{
				format = RenderTextureFormat.ARGB2101010;
			}
#endif
			int topBloomWidth = source.width >> _bloomStartLevel;
			int topBloomHeight = source.height >> _bloomStartLevel;
			_brightness = new RenderTexture(
				TextureUtil.ToPow2RoundUp(topBloomWidth), //2羃でないとミップマップを作れる保証がないので2羃
				TextureUtil.ToPow2RoundUp(topBloomHeight),
				0,
				format);
			_brightness.name = "brightness";
			_brightness.useMipMap = true;
			_brightness.filterMode = FilterMode.Bilinear;
			_bloomRects = new List<BloomRect>();
			int bloomWidth, bloomHeight;
			CalcBloomRenderTextureArrangement(
				out bloomWidth,
				out bloomHeight,
				_bloomRects,
				topBloomWidth,
				topBloomHeight,
				16, // TODO: 調整可能にするか?
				_maxBloomLevelCount);
			Debug.Log("LightPostProcessor.SetupRenderTargets(): create RTs. " + _brightness.width + "x" + _brightness.height + " + " + bloomWidth + "x" + bloomHeight + " levels:" + _bloomRects.Count);
			_bloomX = new RenderTexture(bloomWidth, bloomHeight, 0, format);
			_bloomX.name = "bloomX";
			_bloomX.filterMode = FilterMode.Bilinear;
			_bloomXY = new RenderTexture(bloomWidth, bloomHeight, 0, format);
			_bloomXY.name = "bloomXY";
			_bloomXY.filterMode = FilterMode.Bilinear;
			_bloomCombined = new RenderTexture(bloomWidth, bloomHeight, 0, format);
			_bloomCombined.name = "bloomCombined";

			_prevSource = source;
		}

		void CalcGaussianSamples(float sigma)
		{
			// 0: 0と1
			// 1: -1と0
			// 2: 2と3
			// 3: -3と-2
			// 4: 4と5
			// 5: -5と-4
			// 6: 6と7
			// 7: -7と-6
			float w0 = Gauss(sigma, 0f) * 0.5f; // 2回参照されるので半分
			float w1 = Gauss(sigma, 1f);
			float w2 = Gauss(sigma, 2f);
			float w3 = Gauss(sigma, 3f);
			float w4 = Gauss(sigma, 4f);
			float w5 = Gauss(sigma, 5f);
			float w6 = Gauss(sigma, 6f);
			float w7 = Gauss(sigma, 7f);

			float w01 = w0 + w1;
			float x01 = 0f + (w1 / w01);
			float w23 = w2 + w3;
			float x23 = 2f + (w3 / w23);
			float w45 = w4 + w5;
			float x45 = 4f + (w5 / w45);
			float w67 = w6 + w7;
			float x67 = 6f + (w7 / w67);
			float wSum = (w01 + w23 + w45 + w67) * 2f;
			// 和が1になるように正規化
			w01 /= wSum;
			w23 /= wSum;
			w45 /= wSum;
			w67 /= wSum;
			SetGaussSample(0, x01, w01);
			SetGaussSample(1, x23, w23);
			SetGaussSample(2, x45, w45);
			SetGaussSample(3, x67, w67);
		}

		void SetGaussSample(int index, float offset, float weight)
		{
			var sample = _bloomSamples[index];
			sample.offset = offset;
			sample.weight = weight;
			_bloomSamples[index] = sample;
		}

		float Gauss(float sigma, float x)
		{
			float sigma2 = sigma * sigma;
			return Mathf.Exp(-(x * x) / (2f * sigma2));
		}

		struct BloomSample
		{
			public float offset;
			public float weight;
		}

		struct BloomRect
		{
			public int x;
			public int y;
			public int width;
			public int height;
			public int uvTransformShaderPropertyId;
			public int weightShaderPropertyId;
		}

		void CalcBloomRenderTextureArrangement(
			out int widthOut,
			out int heightOut,
			List<BloomRect> rects,
			int width,
			int height,
			int padding,
			int levelCount)
		{
			bool isRight = (height > width); // 縦長なら右配置から始める
			int x = padding;
			int y = padding;
			int maxX = 0;
			int maxY = 0;
			while ((levelCount > 0) && (width > 0) && (height > 0))
			{
				BloomRect rect;
				rect.x = x;
				rect.y = y;
				rect.width = width;
				rect.height = height;
				rect.uvTransformShaderPropertyId = Shader.PropertyToID("_BloomUvTransform" + rects.Count);
				rect.weightShaderPropertyId = Shader.PropertyToID("_BloomWeight" + rects.Count);
				rects.Add(rect);
				maxX = System.Math.Max(maxX, x + width + padding);
				maxY = System.Math.Max(maxY, y + height + padding);
				if (isRight)
				{
					x += width + padding;
				}
				else
				{
					y += height + padding;
				}
				isRight = !isRight;


				// 4で割れなくなるとサンプリング点がズレて汚なくなるので抜ける。TODO: 理由を明らかにせよ。奇数になるとどうも汚ない。
				if (((width % 4) != 0) || ((height % 4) != 0))
				{
					//				break;
				}
				width /= 2;
				height /= 2;
				// 指定サイズ以下なら作らない
				if ((width < _minBloomLevelSize) || (height < _minBloomLevelSize))
				{
					break;
				}
				levelCount--;
			}
			widthOut = maxX;
			heightOut = maxY;
			_first = true;
		}

		void SetColorTransform()
		{
			if (_compositionMaterial == null)
			{
				return;
			}
			var scaleOffsetTransform = Matrix4x4.Translate(new Vector3(_colorOffset.x, _colorOffset.y, _colorOffset.z))
				* Matrix4x4.Scale(new Vector3(_colorScale.x, _colorScale.y, _colorScale.z));

			var toYuv = new Matrix4x4();
			toYuv.SetRow(0, new Vector4(0.299f, 0.587f, 0.114f, 0f));
			toYuv.SetRow(1, new Vector4(-0.169f, -0.331f, 0.5f, 0f));
			toYuv.SetRow(2, new Vector4(0.5f, -0.419f, -0.081f, 0f));
			toYuv.SetRow(3, new Vector4(0f, 0f, 0f, 1f));

			var saturationTransform = Matrix4x4.Scale(new Vector3(1f, _saturation, _saturation));

			var fromYuv = new Matrix4x4();
			fromYuv.SetRow(0, new Vector4(1f, 0f, 1.402f, 0f));
			fromYuv.SetRow(1, new Vector4(1f, -0.344f, -0.714f, 0f));
			fromYuv.SetRow(2, new Vector4(1f, 1.772f, 0f, 0f));
			fromYuv.SetRow(3, new Vector4(0f, 0f, 0f, 1f));

			Matrix4x4 t = scaleOffsetTransform * fromYuv * saturationTransform * toYuv;
			_compositionMaterial.SetVector("_ColorTransformR", t.GetRow(0));
			_compositionMaterial.SetVector("_ColorTransformG", t.GetRow(1));
			_compositionMaterial.SetVector("_ColorTransformB", t.GetRow(2));
		}

#if UNITY_EDITOR

		int _bloomStartLevelOnSetup;
		int _maxBloomLevelCountOnSetup;
		int _minBloomLevelSizeOnSetup;

		void OnValidate()
		{
			if ((_bloomStartLevelOnSetup != _bloomStartLevel)
				|| (_maxBloomLevelCountOnSetup != _maxBloomLevelCount)
				|| (_minBloomLevelSizeOnSetup != _minBloomLevelSize))
			{
				_bloomStartLevelOnSetup = _bloomStartLevel;
				_maxBloomLevelCountOnSetup = _maxBloomLevelCount;
				_minBloomLevelSizeOnSetup = _minBloomLevelSize;
				_prevSource = null; // これで再初期化が走る
			}
			SetColorTransform();
		}
#endif
	}
}