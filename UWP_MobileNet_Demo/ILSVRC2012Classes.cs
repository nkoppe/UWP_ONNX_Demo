using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;

namespace UWP_MobileNet_Demo
{
	class ILSVRC2012Classes
	{
		public ClassItem[] Classes { get;　private set; }

		public ILSVRC2012Classes()
		{
			var r = ResourceLoader.GetForCurrentView("classes");
			var src = r.GetString("classtext");

			string[] rowlis = src.Split("\n");       //改行コードで分割

			List<ClassItem> lis = new List<ClassItem>();
			for (int i = 0; i < rowlis.Length; i++)
			{
				string[] str = rowlis[i].Split("/");
				lis.Add(new ClassItem(str[0], str[1]));
			}

			Classes = lis.ToArray();
		}
	}

	class ClassItem
	{
		public string Id { get; private set; }
		public string Category { get; private set; }

		/// <summary>
		/// コンストラクタ
		/// </summary>
		/// <param name="id"></param>
		/// <param name="category"></param>
		public ClassItem(string id, string category)
		{
			this.Id = id;
			this.Category = category;
		}

		/// <summary>
		/// カテゴリ名を出力
		/// </summary>
		/// <returns></returns>
		public override string ToString()
		{
			return Category + "(" + Id + ")";
		}
	}
}
