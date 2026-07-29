#include "hash_list.h"
#include <cwctype>
#include <algorithm>

void CHashedStringList::Add(TCHAR * String, TCHAR * Value)
{
	std::wstring buff = String;
	if (!m_bCaseSense)
		std::transform(buff.begin(), buff.end(), buff.begin(), ::towlower);

	strmap::iterator it = stringmap.find(buff);
	if (it == stringmap.end()) {
		stringmap[buff] = _wcsdup(Value);
	}
}

void CHashedStringList::Delete(TCHAR * String)
{
	std::wstring buff = String;
	if (!m_bCaseSense)
		std::transform(buff.begin(), buff.end(), buff.begin(), ::towlower);
	stringmap.erase(buff);
}

TCHAR * CHashedStringList::Find(TCHAR * String)
{
	TCHAR* b = _wcsdup(String);
	if (!b)
		return NULL;
	if (!m_bCaseSense)
		_wcslwr_s(b, wcslen(b) + 1);
	std::wstring buff = b;
	free(b);
	strmap::iterator it = stringmap.find(buff);
	if (it != stringmap.end())
		return it->second;
	else
		return NULL;
}
