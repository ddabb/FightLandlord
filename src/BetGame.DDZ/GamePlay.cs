﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace BetGame.DDZ
{
	public class GamePlay {
		/// <summary>
		/// 唯一标识
		/// </summary>
		public string Id { get; }
		public GameInfo Data { get; }

		public static Action<string, GameInfo> OnSaveData;
		public static Func<string, GameInfo> OnGetData;
		/// <summary>
		/// 洗牌后作二次分析，在这里可以重新洗牌、重新定庄家
		/// </summary>
		public Action<string, GameInfo> OnShuffle;
		/// <summary>
		/// 叫地主阶段，下一位，在这里可以处理机器人自动叫地主、选择农民
		/// </summary>
		public Action<string, GameInfo> OnNextSelect;
		/// <summary>
		/// 斗地主阶段，下一位，在这里可以处理机器人自动出牌
		/// </summary>
		public Action<string, GameInfo> OnNextPlay;
		/// <summary>
		/// 游戏结束，通知前端
		/// </summary>
		public Action<string, GameInfo> OnGameOver;

		private static readonly ThreadLocal<Random> rnd = new ThreadLocal<Random>(() => new Random());

		private GamePlay(string id) {
			if (string.IsNullOrEmpty(id) == false) {
				this.Data = this.EventGetData(id);
				if (this.Data == null) throw new ArgumentException("根据 id 参数找不到斗地主数据");
				this.Id = id;
			} else {
				this.Data = new GameInfo();
				this.Id = Guid.NewGuid().ToString();
			}
		}
		private void EventSave() {
			if (OnSaveData != null) {
				OnSaveData(this.Id, this.Data);
				return;
			}
			RedisHelper.HSet($"DDZrdb", this.Id, this.Data);
		}
		private GameInfo EventGetData(string id) {
			if (OnGetData != null) {
				return OnGetData(id);
			}
			return RedisHelper.HGet<GameInfo>("DDZrdb", id);
		}

		/// <summary>
		/// 创建一局游戏
		/// </summary>
		/// <param name="playerIds"></param>
		/// <param name="multiple"></param>
		/// <param name="multipleAdditionMax"></param>
		/// <returns></returns>
		public static GamePlay Create(string[] playerIds, decimal multiple = 1, decimal multipleAdditionMax = 3) {
			if (playerIds == null) throw new ArgumentException("players 参数不能为空");
			if (playerIds.Length != 3) throw new ArgumentException("players 参数长度必须 3");

			var fl = new GamePlay(null);
			fl.Data.multiple = multiple;
			fl.Data.multipleAdditionMax = multipleAdditionMax;
			fl.Data.dipai = new int[3];
			fl.Data.chupai = new List<HandPokerInfo>();
			fl.Data.stage = GameStage.未开始;
			fl.Data.players = new List<GamePlayer>();

			for (var a = 0; a < playerIds.Length; a++)
				fl.Data.players.Add(new GamePlayer { id = playerIds[a], poker = new List<int>(), pokerInit = new List<int>(), role = GamePlayerRole.未知 });

			fl.EventSave();
			return fl;
		}
		/// <summary>
		/// 查找一局游戏
		/// </summary>
		/// <param name="id"></param>
		/// <returns></returns>
		public static GamePlay GetById(string id) {
			if (string.IsNullOrEmpty(id)) throw new ArgumentException("id 参数不能为空");
			return new GamePlay(id);
		}

		/// <summary>
		/// 洗牌
		/// </summary>
		public void Shuffle() {
			if (this.Data.stage != GameStage.未开始) throw new ArgumentException($"游戏阶段错误，当前阶段：{this.Data.stage}");

			this.Data.multipleAddition = 0;
			this.Data.bong = 0;
			this.Data.stage = GameStage.叫地主;

			//洗牌
			var tmppks = Utils.GetNewPoker();
			var pks = new byte[tmppks.Count];
			for (var a = 0; a < pks.Length; a++) {
				pks[a] = (byte)tmppks[rnd.Value.Next(tmppks.Count)];
				tmppks.Remove(pks[a]);
			}
			//确定庄家，谁先拿牌
			this.Data.playerIndex = rnd.Value.Next(this.Data.players.Count);
			///分牌
			this.Data.dipai[0] = pks[51];
			this.Data.dipai[1] = pks[52];
			this.Data.dipai[2] = pks[53];
			for (int a = 0, b = this.Data.playerIndex; a < 51; a++) {
				this.Data.players[b].poker.Add(pks[a]);
				this.Data.players[b].pokerInit.Add(pks[a]);
				if (++b >= this.Data.players.Count) b = 0;
			}
			this.OnShuffle?.Invoke(this.Id, this.Data); //在此做AI分析
			for (var a = 0; a < this.Data.players.Count; a++) {
				this.Data.players[a].poker.Sort((x, y) => y.CompareTo(x));
			}
			this.EventSave();
			WriteLog($"【洗牌分牌】完毕，进入【叫地主】环节，轮到庄家 {this.Data.players[this.Data.playerIndex].id} 先叫");
			this.OnNextSelect?.Invoke(this.Id, this.Data);
		}
		void WriteLog(object obj) {
			Trace.WriteLine($"[{DateTime.Now.ToString("HH:mm:ss")}] {JsonConvert.SerializeObject(obj).Trim('"')}\r\n{this.Id}: {JsonConvert.SerializeObject(this.Data)}");
		}

		/// <summary>
		/// 叫地主
		/// </summary>
		/// <param name="playerId"></param>
		/// <param name="multiple"></param>
		public void SelectLandlord(string playerId, decimal multiple) {
			if (this.Data.stage != GameStage.叫地主) throw new ArgumentException($"游戏阶段错误，当前阶段：{this.Data.stage}");
			var playerIndex = this.Data.players.FindIndex(a => a.id == playerId);
			if (playerIndex == -1) throw new ArgumentException($"{playerId} 不在本局游戏");
			if (playerIndex != this.Data.playerIndex) throw new ArgumentException($"还没有轮到 {playerId} 叫地主");
			if (multiple <= this.Data.multipleAddition) throw new ArgumentException($"multiple 参数应该 > 当前附加倍数 {this.Data.multipleAddition}");
			if (multiple > this.Data.multipleAdditionMax) throw new ArgumentException($"multiple 参数应该 <= 设定最大附加倍数 {this.Data.multipleAdditionMax}");
			this.Data.multipleAddition = multiple;
			if (this.Data.multipleAddition == this.Data.multipleAdditionMax) {
				this.Data.players[this.Data.playerIndex].role = GamePlayerRole.地主;
				this.Data.players[this.Data.playerIndex].poker.AddRange(this.Data.dipai);
				this.Data.players[this.Data.playerIndex].poker.Sort((x, y) => y.CompareTo(x));
				for (var a = 0; a < this.Data.players.Count; a++) if (this.Data.players[a].role == GamePlayerRole.未知) this.Data.players[a].role = GamePlayerRole.农民;
				this.Data.stage = GameStage.斗地主;
				this.EventSave();
				WriteLog($"{this.Data.players[this.Data.playerIndex].id} 以设定最大附加倍数【叫地主】成功，进入【斗地主】环节，轮到庄家 {this.Data.players[this.Data.playerIndex].id} 出牌");
				this.OnNextPlay?.Invoke(this.Id, this.Data);
			} else {
				while (true) {
					if (++this.Data.playerIndex >= this.Data.players.Count) this.Data.playerIndex = 0;
					if (this.Data.players[this.Data.playerIndex].role == GamePlayerRole.未知) break; //跳过已确定的农民
				}
				if (this.Data.playerIndex == playerIndex) {
					this.Data.players[this.Data.playerIndex].role = GamePlayerRole.地主;
					this.Data.players[this.Data.playerIndex].poker.AddRange(this.Data.dipai);
					this.Data.players[this.Data.playerIndex].poker.Sort((x, y) => y.CompareTo(x));
					this.Data.stage = GameStage.斗地主;
					this.EventSave();
					WriteLog($"{this.Data.players[this.Data.playerIndex].id} 附加倍数{multiple}【叫地主】成功，进入【斗地主】环节，轮到庄家 {this.Data.players[this.Data.playerIndex].id} 出牌");
					this.OnNextSelect?.Invoke(this.Id, this.Data);
				} else {
					this.EventSave();
					WriteLog($"{this.Data.players[playerIndex].id} 【叫地主】 +{this.Data.multipleAddition}倍，轮到 {this.Data.players[this.Data.playerIndex].id} 叫地主");
					this.OnNextSelect?.Invoke(this.Id, this.Data);
				}
			}
		}
		/// <summary>
		/// 不叫地主，选择农民
		/// </summary>
		/// <param name="playerId"></param>
		public void SelectFarmer(string playerId) {
			if (this.Data.stage != GameStage.叫地主) throw new ArgumentException($"游戏阶段错误，当前阶段：{this.Data.stage}");
			var playerIndex = this.Data.players.FindIndex(a => a.id == playerId);
			if (playerIndex == -1) throw new ArgumentException($"{playerId} 不在本局游戏");
			if (playerIndex != this.Data.playerIndex) throw new ArgumentException($"还没有轮到 {playerId} 操作");
			this.Data.players[playerIndex].role = GamePlayerRole.农民;
			var unkonws = this.Data.players.Where(a => a.role == GamePlayerRole.未知).Count();
			if (unkonws == 1 && this.Data.multipleAddition > 0) {
				this.Data.playerIndex = this.Data.players.FindIndex(a => a.role == GamePlayerRole.未知);
				this.Data.players[this.Data.playerIndex].role = GamePlayerRole.地主;
				this.Data.players[this.Data.playerIndex].poker.AddRange(this.Data.dipai);
				this.Data.players[this.Data.playerIndex].poker.Sort((x, y) => y.CompareTo(x));
				for (var a = 0; a < this.Data.players.Count; a++) if (this.Data.players[a].role == GamePlayerRole.未知) this.Data.players[a].role = GamePlayerRole.农民;
				this.Data.stage = GameStage.斗地主;
				this.EventSave();
				WriteLog($"{this.Data.players[playerIndex].id} 选择农民，{this.Data.players[this.Data.playerIndex].id} 【叫地主】成功，进入【斗地主】环节，轮到庄家 {this.Data.players[this.Data.playerIndex].id} 出牌");
				this.OnNextPlay?.Invoke(this.Id, this.Data);
			} else if (unkonws == 0) {
				this.Data.stage = GameStage.游戏结束;
				this.EventSave();
				WriteLog($"所有玩家选择农民，【游戏结束】");
				this.OnGameOver?.Invoke(this.Id, this.Data);
			} else {
				while (true) {
					if (++this.Data.playerIndex >= this.Data.players.Count) this.Data.playerIndex = 0;
					if (this.Data.players[this.Data.playerIndex].role == GamePlayerRole.未知) break; //跳过已确定的农民
				}
				this.EventSave();
				WriteLog($"{this.Data.players[playerIndex].id} 选择农民，轮到 {this.Data.players[this.Data.playerIndex].id} 叫地主");
				this.OnNextSelect?.Invoke(this.Id, this.Data);
			}
		}

		/// <summary>
		/// 提示出牌
		/// </summary>
		/// <param name="playerId"></param>
		/// <returns></returns>
		public List<int[]> PlayTips(string playerId) {
			if (this.Data.stage != GameStage.斗地主) throw new ArgumentException($"游戏阶段错误，当前阶段：{this.Data.stage}");
			var playerIndex = this.Data.players.FindIndex(a => a.id == playerId);
			if (playerIndex == -1) throw new ArgumentException($"{playerId} 不在本局游戏");
			if (playerIndex != this.Data.playerIndex) throw new ArgumentException($"还没有轮到 {playerId} 出牌");
			var uphand = this.Data.chupai.LastOrDefault();
			if (uphand?.playerIndex == this.Data.playerIndex) uphand = null;
			return Utils.GetAllTips(this.Data.players[this.Data.playerIndex].poker, uphand);
		}

		/// <summary>
		/// 出牌
		/// </summary>
		/// <param name="playerId"></param>
		/// <param name="poker"></param>
		public void Play(string playerId, int[] poker) {
			if (this.Data.stage != GameStage.斗地主) throw new ArgumentException($"游戏阶段错误，当前阶段：{this.Data.stage}");
			var playerIndex = this.Data.players.FindIndex(a => a.id == playerId);
			if (playerIndex == -1) throw new ArgumentException($"{playerId} 不在本局游戏");
			if (playerIndex != this.Data.playerIndex) throw new ArgumentException($"还没有轮到 {playerId} 出牌");
			if (poker == null || poker.Length == 0) throw new ArgumentException("poker 不能为空");
			foreach (var pk in poker) if (this.Data.players[this.Data.playerIndex].poker.Contains(pk) == false) throw new ArgumentException($"{playerId} 手上没有这手牌");
			var hand = new HandPokerInfo { time = DateTime.Now, playerIndex = this.Data.playerIndex, result = Utils.ComplierHandPoker(poker) };
			if (hand.result == null) throw new ArgumentException("poker 不是有效的一手牌");
			if (this.Data.chupai.Any() && this.Data.chupai.Last().playerIndex != this.Data.playerIndex && Utils.CompareHandPoker(hand, this.Data.chupai.Last()) <= 0) throw new ArgumentException("poker 打不过上一手牌");
			this.Data.chupai.Add(hand);
			foreach (var pk in poker) this.Data.players[this.Data.playerIndex].poker.Remove(pk);

			if (hand.result.type == HandPokerType.四条炸 || hand.result.type == HandPokerType.王炸) this.Data.bong += 1;

			if (this.Data.players[this.Data.playerIndex].poker.Count == 0) {
				var wealth = this.Data.multiple * (this.Data.multipleAddition + this.Data.bong);
				var dizhuWinner = this.Data.players[this.Data.playerIndex].role == GamePlayerRole.地主;
				this.Data.stage = GameStage.游戏结束;
				this.EventSave();
				WriteLog($"{this.Data.players[playerIndex].id} 出牌 {hand.result.text}，【游戏结束】，{(dizhuWinner? GamePlayerRole.地主 : GamePlayerRole.农民)} 获得了胜利，本局炸弹 {this.Data.bong}个，结算金额 {wealth}");
				this.OnGameOver?.Invoke(this.Id, this.Data);
			} else {
				if (++this.Data.playerIndex >= this.Data.players.Count) this.Data.playerIndex = 0;
				this.EventSave();
				WriteLog($"{this.Data.players[playerIndex].id} 出牌 {hand.result.text}，轮到 {this.Data.players[this.Data.playerIndex].id} 出牌");
				this.OnNextPlay?.Invoke(this.Id, this.Data);
			}
		}

		/// <summary>
		/// 不要
		/// </summary>
		/// <param name="playerId"></param>
		public void Pass(string playerId) {
			if (this.Data.stage != GameStage.斗地主) throw new ArgumentException($"游戏阶段错误，当前阶段：{this.Data.stage}");
			var playerIndex = this.Data.players.FindIndex(a => a.id == playerId);
			if (playerIndex == -1) throw new ArgumentException($"{playerId} 不在本局游戏");
			if (playerIndex != this.Data.playerIndex) throw new ArgumentException($"还没有轮到 {playerId} 出牌");
			if (this.Data.chupai.Any() == false) throw new ArgumentException("第一手牌不能 Pass");
			if (this.Data.chupai.Last().playerIndex == this.Data.playerIndex) throw new ArgumentException("此时应该出牌，不能 Pass");
			if (++this.Data.playerIndex >= this.Data.players.Count) this.Data.playerIndex = 0;
			this.EventSave();
			WriteLog($"{this.Data.players[playerIndex].id} 不要，轮到 {this.Data.players[this.Data.playerIndex].id} 出牌");
			this.OnNextPlay?.Invoke(this.Id, this.Data);
		}
	}
}
