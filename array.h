#pragma once

#ifndef _INC_SHLWAPI
#define NO_SHLWAPI_STRFCNS
#define NO_SHLWAPI_PATH
#define NO_SHLWAPI_REG
#define NO_SHLWAPI_STREAM
#define NO_SHLWAPI_GDI
#include <shlwapi.h>
#endif

#if !defined(_INC_SHLWAPI) || defined(NOSHLWAPI) || defined(NO_SHLWAPI_PATH)
BOOL WINAPI PathIsRelative(LPCTSTR pszPath);
BOOL WINAPI PathRemoveFileSpec(LPTSTR pszPath);
LPTSTR WINAPI PathFindExtension(LPCTSTR pszPath);
LPTSTR WINAPI PathAddBackslash(LPTSTR pszPath);
LPTSTR WINAPI PathCombine(LPTSTR pszDest, LPCTSTR pszDir, LPCTSTR pszFile);
#endif

#include <algorithm>
#include <utility>
#include <vector>

template <class T>
class CArray
{
	std::vector<T> m_values;

public:
	BOOL Add(const T& value)
	{
		try {
			m_values.push_back(value);
			return TRUE;
		}
		catch (...) {
			return FALSE;
		}
	}

	BOOL Remove(const T& value)
	{
		typename std::vector<T>::iterator it =
			std::find(m_values.begin(), m_values.end(), value);
		if (it == m_values.end())
			return FALSE;
		m_values.erase(it);
		return TRUE;
	}

	void RemoveAll()
	{
		m_values.clear();
	}

	int GetSize() const
	{
		return static_cast<int>(m_values.size());
	}

	T& operator[](int index) { return m_values[index]; }
	const T& operator[](int index) const { return m_values[index]; }

	T* Begin() { return m_values.empty() ? NULL : m_values.data(); }
	T* Begin() const
	{
		return m_values.empty() ? NULL : const_cast<T*>(m_values.data());
	}
	T* End() { return m_values.empty() ? NULL : m_values.data() + m_values.size(); }
	T* End() const
	{
		return m_values.empty()
			? NULL
			: const_cast<T*>(m_values.data()) + m_values.size();
	}
	T* GetData() { return Begin(); }
	T* GetData() const { return Begin(); }
};

template <class T>
class CValArray : public CArray<T> {};

template <class T>
class CPtrArray : public CValArray<T*>
{
};

template <class TKey, class TVal>
class CSimpleMap
{
	typedef std::pair<TKey, TVal> Entry;
	std::vector<Entry> m_values;

public:
	BOOL Add(const TKey& key, const TVal& value)
	{
		try {
			m_values.push_back(Entry(key, value));
			return TRUE;
		}
		catch (...) {
			return FALSE;
		}
	}

	int FindKey(const TKey& key) const
	{
		for (size_t i = 0; i < m_values.size(); ++i) {
			if (m_values[i].first == key)
				return static_cast<int>(i);
		}
		return -1;
	}

	TVal& GetValueAt(int index) { return m_values[index].second; }
	const TVal& GetValueAt(int index) const { return m_values[index].second; }
	const TKey& GetKeyAt(int index) const { return m_values[index].first; }
	int GetSize() const { return static_cast<int>(m_values.size()); }
	void RemoveAll() { m_values.clear(); }
};

template <class TKey, class TVal>
class CMap : public CSimpleMap<TKey, TVal>
{
};
